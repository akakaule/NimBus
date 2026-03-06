using Azure.Messaging.ServiceBus;
using NimBus.Core.Messages;
using Microsoft.Azure.Functions.Worker;
using Newtonsoft.Json;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus
{
    public interface IServiceBusSession
    {
        Task CompleteAsync(IServiceBusMessage message, CancellationToken cancellationToken = default);
        Task DeadLetterAsync(IServiceBusMessage message, string reason, string v, CancellationToken cancellationToken = default);
        Task DeferAsync(IServiceBusMessage message, CancellationToken cancellationToken = default);
        Task<IServiceBusMessage> ReceiveDeferredMessageAsync(long nextSequenceNumber, CancellationToken cancellationToken = default);
        Task SetStateAsync(SessionState sessionState, CancellationToken cancellationToken = default);
        Task<SessionState> GetStateAsync(CancellationToken cancellationToken = default);
        Task SendScheduledMessageAsync(Azure.Messaging.ServiceBus.ServiceBusMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default);
    }

    public class ServiceBusSession : IServiceBusSession
    {
        private readonly ServiceBusMessageActions _messageActions;
        private readonly ServiceBusSessionMessageActions _sessionActions;
        private readonly ServiceBusSessionReceiver _sessionReceiver;
        private readonly ServiceBusClient _serviceBusClient;
        private readonly string _entityPath;
        private readonly string _sessionId;
        private ServiceBusSessionReceiver _lazySessionReceiver;
        private readonly SemaphoreSlim _receiverLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Creates a ServiceBusSession for the isolated worker model using ServiceBusSessionMessageActions.
        /// Use this constructor for session-enabled triggers in isolated worker model.
        /// </summary>
        public ServiceBusSession(ServiceBusSessionMessageActions sessionMessageActions, ServiceBusClient serviceBusClient = null, string entityPath = null, string sessionId = null)
        {
            _sessionActions = sessionMessageActions ?? throw new ArgumentNullException(nameof(sessionMessageActions));
            _serviceBusClient = serviceBusClient;
            _entityPath = entityPath;
            _sessionId = sessionId;
        }

        public ServiceBusSession(ServiceBusMessageActions messageActions, ServiceBusSessionMessageActions sessionActions, ServiceBusClient serviceBusClient, string entityPath, string sessionId)
        {
            _messageActions = messageActions ?? throw new ArgumentNullException(nameof(messageActions));
            _sessionActions = sessionActions ?? throw new ArgumentNullException(nameof(sessionActions));
            _serviceBusClient = serviceBusClient;
            _entityPath = entityPath;
            _sessionId = sessionId;
        }

        public ServiceBusSession(ServiceBusSessionReceiver sessionReceiver)
        {
            _sessionReceiver = sessionReceiver ?? throw new ArgumentNullException(nameof(sessionReceiver));
        }

        public Task CompleteAsync(IServiceBusMessage message, CancellationToken cancellationToken = default)
        {
            if (_messageActions != null)
            {
                return _messageActions.CompleteMessageAsync(message.Message, cancellationToken);
            }
            if (_sessionReceiver != null)
            {
                return _sessionReceiver.CompleteMessageAsync(message.Message, cancellationToken);
            }

            throw new InvalidOperationException(
                "Cannot complete message: no ServiceBusMessageActions or ServiceBusSessionReceiver available. " +
                "When using ServiceBusSessionMessageActions, you must also inject ServiceBusMessageActions for message operations.");
        }

        public Task DeadLetterAsync(IServiceBusMessage message, string deadLetterReason, string deadLetterErrorDescription, CancellationToken cancellationToken = default)
        {
            if (_messageActions != null)
            {
                return _messageActions.DeadLetterMessageAsync(message.Message, null, deadLetterReason, deadLetterErrorDescription, cancellationToken);
            }
            if (_sessionReceiver != null)
            {
                return _sessionReceiver.DeadLetterMessageAsync(message.Message, deadLetterReason, deadLetterErrorDescription, cancellationToken);
            }

            throw new InvalidOperationException(
                "Cannot dead-letter message: no ServiceBusMessageActions or ServiceBusSessionReceiver available. " +
                "When using ServiceBusSessionMessageActions, you must also inject ServiceBusMessageActions for message operations.");
        }

        public Task DeferAsync(IServiceBusMessage message, CancellationToken cancellationToken = default)
        {
            if (_messageActions != null)
            {
                return _messageActions.DeferMessageAsync(message.Message, null, cancellationToken);
            }
            if (_sessionReceiver != null)
            {
                return _sessionReceiver.DeferMessageAsync(message.Message, cancellationToken: cancellationToken);
            }

            throw new InvalidOperationException(
                "Cannot defer message: no ServiceBusMessageActions or ServiceBusSessionReceiver available. " +
                "When using ServiceBusSessionMessageActions, you must also inject ServiceBusMessageActions for message operations.");
        }

        public async Task<SessionState> GetStateAsync(CancellationToken cancellationToken = default)
        {
            System.BinaryData sessionData;
            if (_sessionActions != null)
            {
                sessionData = await _sessionActions.GetSessionStateAsync(cancellationToken);
            }
            else
            {
                sessionData = await _sessionReceiver.GetSessionStateAsync(cancellationToken);
            }

            if (sessionData == null || sessionData.ToMemory().Length == 0)
                return new SessionState();

            return sessionData.ToObjectFromJson<SessionState>();
        }

        public async Task SetStateAsync(SessionState sessionState, CancellationToken cancellationToken = default)
        {
            BinaryData sessionData;

            if (!sessionState.IsEmpty())
            {
                string json = JsonConvert.SerializeObject(sessionState);
                sessionData = new BinaryData(Encoding.UTF8.GetBytes(json));
            }
            else
            {
                // Use empty BinaryData instead of null to clear session state.
                // The Azure Functions Worker SDK has a bug where passing null to
                // SetSessionStateAsync causes a NullReferenceException.
                sessionData = new BinaryData(Array.Empty<byte>());
            }

            if (_sessionActions != null)
            {
                await _sessionActions.SetSessionStateAsync(sessionData, cancellationToken);
            }
            else
            {
                await _sessionReceiver.SetSessionStateAsync(sessionData, cancellationToken);
            }
        }

        public async Task<IServiceBusMessage> ReceiveDeferredMessageAsync(long nextSequenceNumber, CancellationToken cancellationToken = default)
        {
            try
            {
                ServiceBusSessionReceiver receiver;

                if (_sessionReceiver != null)
                {
                    receiver = _sessionReceiver;
                }
                else
                {
                    receiver = await GetOrCreateSessionReceiverAsync(cancellationToken);
                }

                var deferredMessages = await receiver.ReceiveDeferredMessagesAsync(new long[] { nextSequenceNumber }, cancellationToken);
                var deferredMessage = deferredMessages.Count > 0 ? deferredMessages[0] : null;

                if (deferredMessage == null)
                    return null;

                return new ServiceBusMessage(deferredMessage);
            }
            catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessageNotFound)
            {
                throw new ServiceBusException(isTransient: true, "Failed to retrieve deferred message. It might be locked by another process.");
            }
        }

        public async Task SendScheduledMessageAsync(Azure.Messaging.ServiceBus.ServiceBusMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
        {
            if (_serviceBusClient == null || string.IsNullOrEmpty(_entityPath))
            {
                throw new InvalidOperationException(
                    "SendScheduledMessageAsync requires a ServiceBusClient and entityPath to be provided. " +
                    "Inject ServiceBusClient via dependency injection and pass it to the ServiceBusAdapter.");
            }

            await using var sender = _serviceBusClient.CreateSender(_entityPath);
            await sender.ScheduleMessageAsync(message, scheduledEnqueueTime, cancellationToken);
        }

        private async Task<ServiceBusSessionReceiver> GetOrCreateSessionReceiverAsync(CancellationToken cancellationToken = default)
        {
            if (_lazySessionReceiver != null)
                return _lazySessionReceiver;

            if (_serviceBusClient == null || string.IsNullOrEmpty(_entityPath) || string.IsNullOrEmpty(_sessionId))
            {
                throw new InvalidOperationException(
                    "ReceiveDeferredMessageAsync requires a ServiceBusClient and entityPath to be provided. " +
                    "Inject ServiceBusClient via dependency injection and pass it to the ServiceBusAdapter.");
            }

            await _receiverLock.WaitAsync(cancellationToken);
            try
            {
                if (_lazySessionReceiver == null)
                {
                    _lazySessionReceiver = await _serviceBusClient.AcceptSessionAsync(_entityPath, _sessionId, cancellationToken: cancellationToken);
                }
                return _lazySessionReceiver;
            }
            finally
            {
                _receiverLock.Release();
            }
        }
    }
}
