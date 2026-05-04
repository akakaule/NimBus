using Azure.Messaging.ServiceBus;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus
{
    public class MessageContext : IMessageContext
    {
        private readonly IServiceBusMessage _sbMessage;
        private readonly IServiceBusSession _sbSession;
        private readonly bool _isDeferred;

        private const int MaxDeadLetterErrorDescriptionLength = 4096;
        private const string TruncationIndicator = "\n...[TRUNCATED]...";
        private const string ThrottleRetryCountProperty = "ThrottleRetryCount";

        public MessageContext(IServiceBusMessage sbMessage, IServiceBusSession sbSession, bool isDeferred = false)
        {
            _sbMessage = sbMessage ?? throw new ArgumentNullException(nameof(sbMessage));
            _sbSession = sbSession ?? throw new ArgumentNullException(nameof(sbSession));

            _isDeferred = isDeferred;
        }

        public string From => GetUserProperty(UserPropertyName.From);

        public string OriginatingFrom
        {
            get { try { return GetUserProperty(UserPropertyName.OriginatingFrom); } catch (InvalidMessageException) { return null; } }
        }

        public string To => GetUserProperty(UserPropertyName.To);

        public string EventId => GetUserProperty(UserPropertyName.EventId);

        public string EventTypeId
        {
            get { try { return GetUserProperty(UserPropertyName.EventTypeId); } catch (InvalidMessageException) { return null; } }
        }

        public string DeadLetterReason
        {
            get { try { return GetUserProperty(UserPropertyName.DeadLetterReason); } catch (InvalidMessageException) { return null; } }
        }
        public string DeadLetterErrorDescription
        {
            get { try { return GetUserProperty(UserPropertyName.DeadLetterErrorDescription); } catch (InvalidMessageException) { return null; } }
        }

        public string OriginatingMessageId => _sbMessage.GetUserProperty(UserPropertyName.OriginatingMessageId) ?? Constants.Self;

        public string ParentMessageId => _sbMessage.GetUserProperty(UserPropertyName.ParentMessageId) ?? Constants.Self;

        public MessageType MessageType => GetMessageType();

        public string MessageId => _sbMessage.MessageId ?? throw new InvalidMessageException($"MessageId is not defined.");

        public string SessionId => _sbMessage.SessionId ?? throw new InvalidMessageException($"SessionId is not defined.");

        public string CorrelationId => _sbMessage.CorrelationId;

        public MessageContent MessageContent => GetContent() ?? throw new InvalidMessageException($"MessageContent is null.");

        public bool IsDeferred => _isDeferred;

        public DateTime EnqueuedTimeUtc => _sbMessage.EnqueuedTimeUtc;

        public int? RetryCount
        {
            get { try { return Int32.Parse(GetUserProperty(UserPropertyName.RetryCount)); } catch (InvalidMessageException) { return null; } catch (FormatException) { return null; } }
        }

        public string OriginalSessionId
        {
            get { try { return GetUserProperty(UserPropertyName.OriginalSessionId); } catch (InvalidMessageException) { return null; } }
        }

        public int? DeferralSequence
        {
            get
            {
                try
                {
                    var value = GetUserProperty(UserPropertyName.DeferralSequence);
                    return value != null ? Int32.Parse(value) : null;
                }
                catch (InvalidMessageException) { return null; }
                catch (FormatException) { return null; }
            }
        }

        public int ThrottleRetryCount
        {
            get
            {
                try
                {
                    var value = _sbMessage.GetUserProperty(ThrottleRetryCountProperty);
                    return value != null ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : 0;
                }
                catch { return 0; }
            }
        }

        // QueueTimeMs / ProcessingTimeMs are measured during this receive — initial
        // value comes from the inbound message's user properties (when the inbound
        // is itself a response that already carries timings, e.g. on the Resolver
        // side), and the receive pipeline overwrites them with locally-measured
        // values via the setter. Plain backing fields, no SB round-trip.
        private long? _queueTimeMs;
        private long? _processingTimeMs;

        public long? QueueTimeMs
        {
            get => _queueTimeMs ?? TryReadLong(UserPropertyName.QueueTimeMs);
            set => _queueTimeMs = value;
        }

        public long? ProcessingTimeMs
        {
            get => _processingTimeMs ?? TryReadLong(UserPropertyName.ProcessingTimeMs);
            set => _processingTimeMs = value;
        }

        public DateTime? HandlerStartedAtUtc { get; set; }

        private long? TryReadLong(UserPropertyName name)
        {
            try
            {
                var value = _sbMessage.GetUserProperty(name);
                if (value is null) return null;
                return long.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
            }
            catch { return null; }
        }


        /// <summary>
        /// We actually don't do anything when Abandon is called.
        /// The intention of abandoning a message is to make a retry attempt, and if we actually call IMessageSession.AbandonAsync, then the lock will be released and the message will be picked up again immediately.
        /// By doing nothing, the lock will expire before the message is picked up again.
        /// </summary>
        public Task Abandon(TransientException exception) => Task.CompletedTask;

        [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
        public async Task BlockSession(CancellationToken cancellationToken = default)
        {
            SessionState state = await GetSessionState(cancellationToken);
            state.BlockedByEventId = EventId;
            await UpdateSessionState(state, cancellationToken);
        }

        [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
        public async Task UnblockSession(CancellationToken cancellationToken = default)
        {
            SessionState state = await GetSessionState(cancellationToken);
            state.BlockedByEventId = null;
            await UpdateSessionState(state, cancellationToken);
        }

        public async Task Complete(CancellationToken cancellationToken = default)
        {
            try
            {
                await _sbSession.CompleteAsync(_sbMessage, cancellationToken);
            }
            catch (ServiceBusException e) when (e.Reason == ServiceBusFailureReason.SessionLockLost)
            {
                throw new TransientException("SessionLockLost exception.", e);
            }
            catch (ServiceBusException e) when (e.IsTransient)
            {
                throw new TransientException("ServiceBus SDK threw transient exception", e);
            }
        }

        private static string FormatExceptionForDeadLetter(Exception exception)
        {
            if (exception == null)
                return null;

            string fullText = exception.ToString();

            if (fullText.Length <= MaxDeadLetterErrorDescriptionLength)
                return fullText;

            int availableLength = MaxDeadLetterErrorDescriptionLength - TruncationIndicator.Length;
            int firstNewlineIndex = fullText.IndexOf('\n');

            if (firstNewlineIndex > 0 && firstNewlineIndex < availableLength)
            {
                string truncated = fullText.Substring(0, availableLength);
                int lastNewline = truncated.LastIndexOf('\n');
                if (lastNewline > firstNewlineIndex)
                    truncated = truncated.Substring(0, lastNewline);

                return truncated + TruncationIndicator;
            }

            return fullText.Substring(0, availableLength) + TruncationIndicator;
        }

        public async Task DeadLetter(string reason, Exception exception = null, CancellationToken cancellationToken = default)
        {
            try
            {
                await _sbSession.DeadLetterAsync(_sbMessage, reason, FormatExceptionForDeadLetter(exception), cancellationToken);
            }
            catch (ServiceBusException e) when (e.Reason == ServiceBusFailureReason.SessionLockLost)
            {
                throw new TransientException("SessionLockLost exception.", e);
            }
            catch (ServiceBusException e) when (e.IsTransient)
            {
                throw new TransientException("ServiceBus SDK threw transient exception", e);
            }
        }

        public async Task Defer(CancellationToken cancellationToken = default)
        {
            if (IsDeferred)
                throw new NotSupportedException("Is already deferred.");

            SessionState state = await GetSessionState(cancellationToken);
            state.DeferredSequenceNumbers.Add(_sbMessage.SequenceNumber);
            await UpdateSessionState(state, cancellationToken);

            try
            {
                await _sbSession.DeferAsync(_sbMessage, cancellationToken);
            }
            catch (ServiceBusException e) when (e.Reason == ServiceBusFailureReason.SessionLockLost)
            {
                throw new TransientException("SessionLockLost exception.", e);
            }
            catch (ServiceBusException e) when (e.IsTransient)
            {
                throw new TransientException("ServiceBus SDK threw transient exception", e);
            }
        }

        public async Task DeferOnly(CancellationToken cancellationToken = default)
        {
            if (IsDeferred)
                throw new NotSupportedException("Is already deferred.");

            try
            {
                await _sbSession.DeferAsync(_sbMessage, cancellationToken);
            }
            catch (ServiceBusException e) when (e.Reason == ServiceBusFailureReason.SessionLockLost)
            {
                throw new TransientException("SessionLockLost exception.", e);
            }
            catch (ServiceBusException e) when (e.IsTransient)
            {
                throw new TransientException("ServiceBus SDK threw transient exception", e);
            }
        }

        [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
        public async Task<bool> IsSessionBlocked(CancellationToken cancellationToken = default)
        {
            SessionState state = await GetSessionState(cancellationToken);
            return !string.IsNullOrEmpty(state.BlockedByEventId)
                || state.DeferredSequenceNumbers.Any();
        }

        [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
        public async Task<bool> IsSessionBlockedByEventId(CancellationToken cancellationToken = default)
        {
            SessionState state = await GetSessionState(cancellationToken);
            return !string.IsNullOrEmpty(state.BlockedByEventId);
        }

        [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
        public async Task<bool> IsSessionBlockedByThis(CancellationToken cancellationToken = default)
        {
            SessionState state = await GetSessionState(cancellationToken);
            return !string.IsNullOrEmpty(state.BlockedByEventId) && state.BlockedByEventId.Equals(EventId, StringComparison.OrdinalIgnoreCase);
        }

        [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
        public async Task<string> GetBlockedByEventId(CancellationToken cancellationToken = default)
        {
            SessionState state = await GetSessionState(cancellationToken);
            return state.BlockedByEventId;
        }

        public async Task<IMessageContext> ReceiveNextDeferred(CancellationToken cancellationToken = default)
        {
            SessionState state = await GetSessionState(cancellationToken);
            while (state.DeferredSequenceNumbers.Any() && !cancellationToken.IsCancellationRequested)
            {
                long nextSequenceNumber = state.DeferredSequenceNumbers.First();

                try
                {
                    IServiceBusMessage deferred = await _sbSession.ReceiveDeferredMessageAsync(nextSequenceNumber, cancellationToken);
                    if (deferred == null)
                    {
                        // Deferred message does not exist.
                        // Update session state by removing the "null reference".
                        state.DeferredSequenceNumbers.RemoveRange(index: 0, count: 1);
                        await UpdateSessionState(state, cancellationToken);

                        continue;
                    }
                    return new MessageContext(deferred, _sbSession, isDeferred: true);
                }
                catch (ServiceBusException e) when (e.Reason == ServiceBusFailureReason.SessionLockLost)
                {
                    throw new TransientException("SessionLockLost exception.", e);
                }
                catch (ServiceBusException e) when (e.IsTransient)
                {
                    throw new TransientException("ServiceBus SDK threw transient exception", e);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        public async Task<IMessageContext> ReceiveNextDeferredWithPop(CancellationToken cancellationToken = default)
        {
            SessionState state = await GetSessionState(cancellationToken);
            while (state.DeferredSequenceNumbers.Any() && !cancellationToken.IsCancellationRequested)
            {
                long nextSequenceNumber = state.DeferredSequenceNumbers.First();

                try
                {
                    IServiceBusMessage deferred = await _sbSession.ReceiveDeferredMessageAsync(nextSequenceNumber, cancellationToken);
                    if (deferred == null)
                    {
                        // Deferred message does not exist.
                        // Update session state by removing the "null reference".
                        state.DeferredSequenceNumbers.RemoveRange(index: 0, count: 1);
                        await UpdateSessionState(state, cancellationToken);

                        continue;
                    }
                    else
                    {
                        state.DeferredSequenceNumbers.RemoveRange(index: 0, count: 1);
                        await UpdateSessionState(state, cancellationToken);
                    }

                    return new MessageContext(deferred, _sbSession, isDeferred: true);
                }
                catch (ServiceBusException e) when (e.Reason == ServiceBusFailureReason.SessionLockLost)
                {
                    throw new TransientException("SessionLockLost exception.", e);
                }
                catch (ServiceBusException e) when (e.IsTransient)
                {
                    throw new TransientException("ServiceBus SDK threw transient exception", e);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        private string GetUserProperty(UserPropertyName userPropertyName)
        {
            return _sbMessage.GetUserProperty(userPropertyName) ?? throw new InvalidMessageException($"Message.UserProperties[{userPropertyName}] is not defined.");
        }

        private MessageType GetMessageType()
        {
            var messageTypeString = GetUserProperty(UserPropertyName.MessageType);

            if (Enum.TryParse(messageTypeString, out MessageType messageType))
                return messageType;

            throw new InvalidMessageException($"Unable to parse MessageType from '{messageTypeString}'.");
        }

        private MessageContent GetContent() =>
            JsonConvert.DeserializeObject<MessageContent>(Encoding.UTF8.GetString(_sbMessage.Body), Core.Messages.Constants.SafeJsonSettings);


        private async Task UpdateSessionState(SessionState sessionState, CancellationToken cancellationToken = default)
        {
            try
            {
                await _sbSession.SetStateAsync(sessionState, cancellationToken);
            }
            catch (ServiceBusException e) when (e.Reason == ServiceBusFailureReason.SessionLockLost)
            {
                throw new TransientException("SessionLockLost exception.", e);
            }
            catch (ServiceBusException e) when (e.IsTransient)
            {
                throw new TransientException("ServiceBus SDK threw transient exception", e);
            }
        }

        private async Task<SessionState> GetSessionState(CancellationToken cancellationToken = default)
        {
            try
            {
                return await _sbSession.GetStateAsync(cancellationToken);
            }
            catch (ServiceBusException e) when (e.Reason == ServiceBusFailureReason.SessionLockLost)
            {
                throw new TransientException("SessionLockLost exception.", e);
            }
            catch (ServiceBusException e) when (e.IsTransient)
            {
                throw new TransientException("ServiceBus SDK threw transient exception", e);
            }
        }

        [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
        public async Task<int> GetNextDeferralSequenceAndIncrement(CancellationToken cancellationToken = default)
        {
            SessionState state = await GetSessionState(cancellationToken);
            int sequence = state.NextDeferralSequence;
            state.NextDeferralSequence++;
            await UpdateSessionState(state, cancellationToken);
            return sequence;
        }

        [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
        public async Task IncrementDeferredCount(CancellationToken cancellationToken = default)
        {
            SessionState state = await GetSessionState(cancellationToken);
            state.DeferredCount++;
            await UpdateSessionState(state, cancellationToken);
        }

        [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
        public async Task DecrementDeferredCount(CancellationToken cancellationToken = default)
        {
            SessionState state = await GetSessionState(cancellationToken);
            if (state.DeferredCount > 0)
            {
                state.DeferredCount--;
                await UpdateSessionState(state, cancellationToken);
            }
        }

        [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
        public async Task<int> GetDeferredCount(CancellationToken cancellationToken = default)
        {
            SessionState state = await GetSessionState(cancellationToken);
            return state.DeferredCount;
        }

        [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
        public async Task<bool> HasDeferredMessages(CancellationToken cancellationToken = default)
        {
            SessionState state = await GetSessionState(cancellationToken);
            return state.HasDeferredMessages();
        }

        [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
        public async Task ResetDeferredCount(CancellationToken cancellationToken = default)
        {
            SessionState state = await GetSessionState(cancellationToken);
            state.DeferredCount = 0;
            await UpdateSessionState(state, cancellationToken);
        }

        public async Task ScheduleRedelivery(TimeSpan delay, int throttleRetryCount, CancellationToken cancellationToken = default)
        {
            // Create new message with same content
            var receivedMessage = _sbMessage.Message;
            var newMessage = new Azure.Messaging.ServiceBus.ServiceBusMessage(_sbMessage.Body)
            {
                SessionId = SessionId,
                CorrelationId = CorrelationId,
                MessageId = Guid.NewGuid().ToString(),
                // Copy standard Service Bus properties
                ContentType = receivedMessage.ContentType,
                Subject = receivedMessage.Subject,
                ReplyTo = receivedMessage.ReplyTo,
                ReplyToSessionId = receivedMessage.ReplyToSessionId,
                PartitionKey = receivedMessage.PartitionKey
            };

            // Copy all application properties from original message
            foreach (var prop in receivedMessage.ApplicationProperties)
            {
                newMessage.ApplicationProperties[prop.Key] = prop.Value;
            }

            // Set/update throttle retry count
            newMessage.ApplicationProperties[ThrottleRetryCountProperty] = throttleRetryCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Schedule for future delivery
            var scheduledTime = DateTimeOffset.UtcNow.Add(delay);
            try
            {
                await _sbSession.SendScheduledMessageAsync(newMessage, scheduledTime, cancellationToken);
                // Complete original message only after successful scheduling
                await Complete(cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // SendScheduledMessageAsync requires ServiceBusClient + entityPath which may not be available
                // in all handler configurations (e.g., ServiceBusSessionReceiver path).
                // Fall back to letting the message retry via lock expiration - don't complete it.
                // The message will be redelivered after the lock expires (~30s).
                throw new Core.Messages.Exceptions.TransientException(
                    "Scheduled redelivery not available in current configuration. Message will retry after lock expiration.");
            }
        }
    }
}
