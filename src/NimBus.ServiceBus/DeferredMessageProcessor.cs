using Azure.Messaging.ServiceBus;
using NimBus.Core.Messages;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus
{
    /// <summary>
    /// Processes deferred messages from the session-enabled deferred subscription.
    /// </summary>
    public class DeferredMessageProcessor : IDeferredMessageProcessor
    {
        private readonly ServiceBusClient _serviceBusClient;
        private readonly string _deferredSubscriptionName;
        private const int BatchSize = 100;

        public DeferredMessageProcessor(ServiceBusClient serviceBusClient, string deferredSubscriptionName = null)
        {
            _serviceBusClient = serviceBusClient ?? throw new ArgumentNullException(nameof(serviceBusClient));
            _deferredSubscriptionName = deferredSubscriptionName ?? Constants.DeferredSubscriptionName;
        }

        /// <summary>
        /// Processes all deferred messages for the specified session.
        /// Messages are retrieved from the session-enabled deferred subscription using AcceptSessionAsync,
        /// sorted by DeferralSequence, and re-published to the main topic for normal processing.
        /// </summary>
        public async Task ProcessDeferredMessagesAsync(string sessionId, string topicName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new ArgumentException("Session ID is required", nameof(sessionId));
            if (string.IsNullOrEmpty(topicName))
                throw new ArgumentException("Topic name is required", nameof(topicName));

            ServiceBusSessionReceiver receiver;
            try
            {
                // Accept the specific session - only receives messages for this sessionId
                receiver = await _serviceBusClient.AcceptSessionAsync(topicName, _deferredSubscriptionName, sessionId, cancellationToken: cancellationToken);
            }
            catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
            {
                // No messages for this session - nothing to process
                return;
            }

            await using (receiver)
            await using (var sender = _serviceBusClient.CreateSender(topicName))
            {
                try
                {
                    // Receive messages in batches until we've processed all messages for this session
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var messages = await receiver.ReceiveMessagesAsync(BatchSize, TimeSpan.FromSeconds(5), cancellationToken);
                        if (messages == null || messages.Count == 0)
                            break;

                        // Sort by DeferralSequence to ensure FIFO ordering
                        var orderedMessages = messages.OrderBy(m => GetDeferralSequence(m)).ToList();

                        foreach (var message in orderedMessages)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            // Re-publish to main topic for normal processing
                            var republishedMessage = CreateRepublishedMessage(message, sessionId);
                            await sender.SendMessageAsync(republishedMessage, cancellationToken);

                            // Complete the deferred message
                            await receiver.CompleteMessageAsync(message, cancellationToken);
                        }

                        // If we received fewer messages than BatchSize, we've processed all available
                        if (messages.Count < BatchSize)
                            break;
                    }
                }
                catch (ServiceBusException ex) when (ex.IsTransient)
                {
                    throw new Core.Messages.Exceptions.TransientException("Transient error processing deferred messages", ex);
                }
            }
        }

        private static int GetDeferralSequence(ServiceBusReceivedMessage message)
        {
            if (message.ApplicationProperties.TryGetValue(UserPropertyName.DeferralSequence.ToString(), out var value))
            {
                if (value is int intValue)
                    return intValue;
                if (int.TryParse(value?.ToString(), out var parsed))
                    return parsed;
            }
            return 0;
        }

        private static Azure.Messaging.ServiceBus.ServiceBusMessage CreateRepublishedMessage(ServiceBusReceivedMessage deferredMessage, string sessionId)
        {
            var result = new Azure.Messaging.ServiceBus.ServiceBusMessage(deferredMessage.Body);

            // Copy all application properties except the deferred-specific ones
            foreach (var prop in deferredMessage.ApplicationProperties)
            {
                if (prop.Key != UserPropertyName.OriginalSessionId.ToString() &&
                    prop.Key != UserPropertyName.DeferralSequence.ToString())
                {
                    result.ApplicationProperties[prop.Key] = prop.Value;
                }
            }

            // Set the session ID so it goes back to the correct session
            result.SessionId = sessionId;
            result.CorrelationId = deferredMessage.CorrelationId;

            return result;
        }
    }
}
