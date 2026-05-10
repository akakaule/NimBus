using Azure.Messaging.ServiceBus;
using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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

            using var replaySpan = NimBusActivitySources.DeferredProcessor.StartActivity(
                "NimBus.DeferredProcessor.Replay", ActivityKind.Internal);
            if (replaySpan is not null)
            {
                replaySpan.SetTag(MessagingAttributes.NimBusEndpoint, topicName);
                replaySpan.SetTag(MessagingAttributes.NimBusSessionKey, sessionId);
            }

            int totalReplayed = 0;
            try
            {
                ServiceBusSessionReceiver receiver;
                try
                {
                    // Accept the specific session - only receives messages for this sessionId
                    receiver = await _serviceBusClient.AcceptSessionAsync(topicName, _deferredSubscriptionName, sessionId, cancellationToken: cancellationToken);
                }
                catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
                {
                    // No messages for this session - nothing to process. Graceful no-op:
                    // the span ends as Ok with batch_size=0 in the finally block below.
                    replaySpan?.SetStatus(ActivityStatusCode.Ok);
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

                            var batchTimestamp = Stopwatch.GetTimestamp();

                            foreach (var message in orderedMessages)
                            {
                                cancellationToken.ThrowIfCancellationRequested();

                                // Re-publish to main topic for normal processing
                                var republishedMessage = CreateRepublishedMessage(message, sessionId, topicName);
                                await sender.SendMessageAsync(republishedMessage, cancellationToken);

                                // Complete the deferred message
                                await receiver.CompleteMessageAsync(message, cancellationToken);
                            }

                            var batchElapsedMs = Stopwatch.GetElapsedTime(batchTimestamp).TotalMilliseconds;
                            var endpointTag = BuildEndpointTag(topicName);
                            NimBusMeters.DeferredReplayed.Add(orderedMessages.Count, endpointTag);
                            NimBusMeters.DeferredReplayDuration.Record(batchElapsedMs, endpointTag);
                            totalReplayed += orderedMessages.Count;

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

                replaySpan?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex) when (replaySpan is not null)
            {
                replaySpan.SetStatus(ActivityStatusCode.Error, ex.Message);
                replaySpan.SetTag(MessagingAttributes.ErrorType, ex.GetType().FullName);
                throw;
            }
            finally
            {
                replaySpan?.SetTag(MessagingAttributes.NimBusDeferredBatchSize, totalReplayed);
            }
        }

        private static KeyValuePair<string, object?>[] BuildEndpointTag(string endpoint) =>
            new[] { new KeyValuePair<string, object?>(MessagingAttributes.NimBusEndpoint, endpoint) };

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

        private static Azure.Messaging.ServiceBus.ServiceBusMessage CreateRepublishedMessage(ServiceBusReceivedMessage deferredMessage, string sessionId, string topicName)
        {
            var result = new Azure.Messaging.ServiceBus.ServiceBusMessage(deferredMessage.Body);

            // Copy all application properties except the deferred-specific ones.
            // We also drop "To" because the inbound deferred message's To is "Deferred"
            // (set by SendToDeferredSubscription so the message routes to the Deferred
            // subscription on the topic). Re-using that value would route the republish
            // back into the Deferred subscription instead of the main endpoint.
            foreach (var prop in deferredMessage.ApplicationProperties)
            {
                if (prop.Key != UserPropertyName.OriginalSessionId.ToString() &&
                    prop.Key != UserPropertyName.DeferralSequence.ToString() &&
                    prop.Key != UserPropertyName.To.ToString())
                {
                    result.ApplicationProperties[prop.Key] = prop.Value;
                }
            }

            // Restore To to the destination endpoint so the main subscription's
            // `user.To = '<endpointId>'` filter matches. In NimBus's topology, topic
            // name equals endpoint name (one topic per endpoint).
            result.ApplicationProperties[UserPropertyName.To.ToString()] = topicName;

            // Set the session ID so it goes back to the correct session
            result.SessionId = sessionId;
            result.CorrelationId = deferredMessage.CorrelationId;

            return result;
        }
    }
}
