using Serilog;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Broker.Services
{
    public class ResolverService : IMessageHandler
    {
        private readonly IMessageTrackingStore _store;
        private readonly IMessageStateChangeNotifier _notifier;
        private readonly ILogger _logger;

        private const int MaxThrottleRetries = 10;
        private const int BaseDelaySeconds = 5;
        private const int MaxDelaySeconds = 300; // 5 minutes

        private static readonly Dictionary<MessageType, ResolutionStatus> MessageTypeToStatusMap = new()
        {
            [MessageType.EventRequest] = ResolutionStatus.Pending,
            [MessageType.ResubmissionRequest] = ResolutionStatus.Pending,
            [MessageType.RetryRequest] = ResolutionStatus.Pending,
            [MessageType.SkipRequest] = ResolutionStatus.Pending,
            [MessageType.ContinuationRequest] = ResolutionStatus.Pending,
            // PendingHandoff control flow. The response from the subscriber records
            // the audit row as Pending+Handoff; the two Manager-issued requests are
            // recorded as Pending audit rows that flip when their resulting
            // ResolutionResponse / ErrorResponse arrive (via the existing path).
            [MessageType.PendingHandoffResponse] = ResolutionStatus.Pending,
            [MessageType.HandoffCompletedRequest] = ResolutionStatus.Pending,
            [MessageType.HandoffFailedRequest] = ResolutionStatus.Pending,
            [MessageType.ErrorResponse] = ResolutionStatus.Failed,
            [MessageType.ResolutionResponse] = ResolutionStatus.Completed,
            [MessageType.DeferralResponse] = ResolutionStatus.Deferred,
            [MessageType.SkipResponse] = ResolutionStatus.Skipped,
            [MessageType.UnsupportedResponse] = ResolutionStatus.Unsupported,
        };

        public ResolverService(IMessageTrackingStore store, IMessageStateChangeNotifier notifier = null, ILogger logger = null)
        {
            _store = store;
            _notifier = notifier ?? new NoopMessageStateChangeNotifier();
            _logger = logger;
        }

        public async Task Handle(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            _logger?.Verbose("Resolver: Handle {EventTypeId} EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
                messageContext.MessageContent.EventContent?.EventTypeId, messageContext.EventId, messageContext.MessageId, messageContext.SessionId);

            try
            {
                MessageEntity messageEntity = await CreateMessageEntity(messageContext);

                await _store.StoreMessage(messageEntity);

                var status = await UpdateState(messageEntity);

                _logger?.Information("Resolver: Updated Endpoint EndpointId:{EndpointId}, Status:{Status}, EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
                    messageEntity.EndpointId, status, messageEntity.EventId, messageContext.MessageId, messageEntity.SessionId);

                // Fire state-change notification (provider-neutral). Webhook is no longer
                // the only way for the WebApp to learn about updates; this works for any
                // storage provider including SQL Server which has no Change Feed.
                try { await _notifier.NotifyEndpointStateChangedAsync(messageEntity.EndpointId, cancellationToken); }
                catch (Exception notifyEx) { _logger?.Warning(notifyEx, "Resolver: state-change notification failed (non-fatal)"); }

                await messageContext.Complete(cancellationToken);
            }
            catch (StorageProviderTransientException ex) when (ex.RetryAfter.HasValue)
            {
                await HandleThrottling(messageContext, ex.RetryAfter, cancellationToken);
            }
            catch (TransientException transientException)
            {
                _logger?.Error(transientException, "Resolver: Transient exception EventId:{EventId}", messageContext.EventId);
                await messageContext.Abandon(transientException);
            }
            catch (Exception unexpectedException)
            {
                _logger?.Error(unexpectedException, "Resolver: Failed to handle message, add to DeadLetter. EventId:{EventId}", messageContext.EventId);
                await messageContext.DeadLetter("Failed to handle message.", unexpectedException, cancellationToken);
            }
        }

        private async Task HandleThrottling(IMessageContext messageContext, TimeSpan? retryAfter, CancellationToken cancellationToken)
        {
            var retryCount = messageContext.ThrottleRetryCount;

            if (retryCount >= MaxThrottleRetries)
            {
                _logger?.Error("Resolver: Max throttle retries ({MaxRetries}) exceeded. DeadLettering. EventId:{EventId}, SessionId:{SessionId}",
                    MaxThrottleRetries, messageContext.EventId, messageContext.SessionId);
                await messageContext.DeadLetter("Max throttle retries exceeded", null, cancellationToken);
                return;
            }

            // Calculate exponential backoff: 5s, 10s, 20s, 40s, ... up to 300s
            var calculatedDelay = TimeSpan.FromSeconds(
                Math.Min(BaseDelaySeconds * Math.Pow(2, retryCount), MaxDelaySeconds));

            // Use Cosmos retry-after if longer
            var useCosmosRetryAfter = retryAfter.HasValue && retryAfter.Value > calculatedDelay;
            var delay = useCosmosRetryAfter ? retryAfter.Value : calculatedDelay;

            _logger?.Verbose(
                "Resolver: Throttle delay decision - using {DelaySource}. CosmosRetryAfter:{CosmosRetryAfter}s, CalculatedBackoff:{CalculatedBackoff}s, EventId:{EventId}",
                useCosmosRetryAfter ? "CosmosRetryAfter" : "CalculatedBackoff",
                retryAfter?.TotalSeconds,
                calculatedDelay.TotalSeconds,
                messageContext.EventId);

            _logger?.Information(
                "Resolver: Cosmos DB throttled. Scheduling redelivery in {DelaySeconds}s. EventId:{EventId}, SessionId:{SessionId}, RetryCount:{RetryCount}/{MaxRetries}",
                delay.TotalSeconds, messageContext.EventId, messageContext.SessionId, retryCount + 1, MaxThrottleRetries);

            try
            {
                await messageContext.ScheduleRedelivery(delay, retryCount + 1, cancellationToken);
            }
            catch (TransientException ex)
            {
                _logger?.Information(ex, "Resolver: Failed to schedule redelivery. Abandoning for retry. EventId:{EventId}, SessionId:{SessionId}",
                    messageContext.EventId, messageContext.SessionId);
                await messageContext.Abandon(ex);
            }
        }

        private async Task<MessageEntity> CreateMessageEntity(IReceivedMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);

            if (message.MessageType == MessageType.RetryRequest)
            {
                var messageAudit = new MessageAuditEntity() { AuditorName = Constants.ManagerId, AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Retry };
                await _store.StoreMessageAudit(message.EventId, messageAudit);
            }

            var (endpointId, endpointRole) = DetermineEndpoint(message);

            return new MessageEntity
            {
                EventId = message.EventId,
                MessageId = message.MessageId,
                OriginatingMessageId = message.OriginatingMessageId,
                ParentMessageId = message.ParentMessageId,
                From = message.From,
                To = message.To,
                SessionId = message.SessionId,
                CorrelationId = message.CorrelationId,
                EnqueuedTimeUtc = message.EnqueuedTimeUtc,
                MessageContent = message.MessageContent,
                MessageType = message.MessageType,
                EndpointId = endpointId,
                EndpointRole = endpointRole,
                DeadLetterErrorDescription = message.DeadLetterErrorDescription,
                DeadLetterReason = message.DeadLetterReason,
                EventTypeId = message.EventTypeId ?? message?.MessageContent?.EventContent?.EventTypeId,
                // Per-message timings carried on the response message by the
                // subscriber. Null on EventRequest / original publishes.
                QueueTimeMs = message.QueueTimeMs,
                ProcessingTimeMs = message.ProcessingTimeMs,
                // PendingHandoff metadata. Sub-status is set only on the
                // PendingHandoffResponse audit row (the original Pending+Handoff
                // entry); the Manager-issued HandoffCompleted/HandoffFailed
                // requests are recorded as plain Pending so the subsequent
                // ResolutionResponse / ErrorResponse can flip the original.
                HandoffReason = message.HandoffReason,
                ExternalJobId = message.ExternalJobId,
                ExpectedBy = message.ExpectedBy,
                PendingSubStatus = message.MessageType == MessageType.PendingHandoffResponse ? "Handoff" : null,
            };
        }

        /// <summary>
        /// Determines the endpoint ID and role based on message properties.
        /// </summary>
        /// <remarks>
        /// Endpoint determination rules:
        /// 1. If message is from Broker with ErrorResponse type → Publisher role, use OriginatingFrom as endpoint
        /// 2. If message is from Broker → use To as endpoint (Subscriber role)
        /// 3. If message is a request type (EventRequest, ContinuationRequest, RetryRequest, ResubmissionRequest, SkipRequest) → use To as endpoint
        /// 4. Otherwise → use From as endpoint (response from subscriber)
        /// </remarks>
        internal (string endpointId, EndpointRole role) DetermineEndpoint(IReceivedMessage message)
        {
            var endpointRole = EndpointRole.Subscriber;
            string endpointId;

            // Request types are directed to the subscriber (use To)
            if (message.MessageType == MessageType.EventRequest ||
                     message.MessageType == MessageType.ContinuationRequest ||
                     message.MessageType == MessageType.RetryRequest ||
                     message.MessageType == MessageType.ResubmissionRequest ||
                     message.MessageType == MessageType.SkipRequest ||
                     message.MessageType == MessageType.HandoffCompletedRequest ||
                     message.MessageType == MessageType.HandoffFailedRequest)
            {
                endpointId = message.To;
            }
            // Response types come from the subscriber (use From)
            else
            {
                endpointId = message.From;
            }

            return (endpointId, endpointRole);
        }

        private UnresolvedEvent CreateUnresolvedEvent(MessageEntity message, long? processingTimeMsOverride = null)
        {
            return new UnresolvedEvent
            {
                UpdatedAt = DateTime.UtcNow,
                EnqueuedTimeUtc = message.EnqueuedTimeUtc,

                EventId = message.EventId,
                SessionId = message.SessionId,
                CorrelationId = message.CorrelationId,

                ResolutionStatus = GetResultingStatus(message),
                EndpointRole = message.EndpointRole,
                EndpointId = message.EndpointId,
                RetryCount = message.RetryCount,
                RetryLimit = message.RetryLimit,
                MessageType = message.MessageType,
                DeadLetterReason = message.DeadLetterReason,
                DeadLetterErrorDescription = message.DeadLetterErrorDescription,

                LastMessageId = message.MessageId,
                OriginatingMessageId = message.OriginatingMessageId,
                ParentMessageId = message.ParentMessageId,
                Reason = message.DeadLetterErrorDescription,
                OriginatingFrom = message.OriginatingFrom,

                EventTypeId = message.EventTypeId,
                To = message.To,
                From = message.From,
                MessageContent = message.MessageContent,
                QueueTimeMs = message.QueueTimeMs,
                // For terminal settlement of an event that went through async
                // handoff, override the per-hop handler duration with the
                // wall-clock span from the original EventRequest. The raw value
                // remains on the per-message MessageEntity row for auditing.
                ProcessingTimeMs = processingTimeMsOverride ?? message.ProcessingTimeMs,
                // PendingHandoff metadata: copy through from the projected
                // MessageEntity so the UnresolvedEvent (i.e. the audit row
                // surfaced by the WebApp) carries them too.
                PendingSubStatus = message.PendingSubStatus,
                HandoffReason = message.HandoffReason,
                ExternalJobId = message.ExternalJobId,
                ExpectedBy = message.ExpectedBy,
            };
        }

        // Returns the wall-clock duration since the original EventRequest's
        // EnqueuedTimeUtc when settling a terminal response (Resolution/Error)
        // for an event that previously emitted a PendingHandoffResponse.
        // Returns null otherwise so the regular per-hop ProcessingTimeMs wins.
        private async Task<long?> ComputeHandoffWallClockMsIfTerminal(MessageEntity message)
        {
            if (message.MessageType != MessageType.ResolutionResponse &&
                message.MessageType != MessageType.ErrorResponse)
            {
                return null;
            }

            var history = (await _store.GetEventHistory(message.EventId)).ToList();
            if (!history.Any(m => m.MessageType == MessageType.PendingHandoffResponse))
            {
                return null;
            }

            var eventRequest = history.FirstOrDefault(m => m.MessageType == MessageType.EventRequest);
            if (eventRequest is null)
            {
                return null;
            }

            return (long)Math.Max(0, (DateTime.UtcNow - eventRequest.EnqueuedTimeUtc).TotalMilliseconds);
        }

        private async Task<ResolutionStatus> UpdateState(MessageEntity message)
        {
            ResolutionStatus status = GetResultingStatus(message);
            long? wallClockMs = await ComputeHandoffWallClockMsIfTerminal(message);
            UnresolvedEvent unresolvedEvent = CreateUnresolvedEvent(message, wallClockMs);

            var statusHandlers = new Dictionary<ResolutionStatus, Func<Task>>
            {
                [ResolutionStatus.Completed] = () => _store.UploadCompletedMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
                [ResolutionStatus.Skipped] = () => _store.UploadSkippedMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
                [ResolutionStatus.Failed] = () => _store.UploadFailedMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
                [ResolutionStatus.Deferred] = () => _store.UploadDeferredMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
                [ResolutionStatus.Pending] = () => _store.UploadPendingMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
                [ResolutionStatus.DeadLettered] = () => _store.UploadDeadletteredMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
                [ResolutionStatus.Unsupported] = () => _store.UploadUnsupportedMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
            };

            if (statusHandlers.TryGetValue(status, out var handler))
            {
                await handler();
            }

            return status;
        }

        private ResolutionStatus GetResultingStatus(MessageEntity message)
        {
            if (message.DeadLetterErrorDescription != null)
            {
                return ResolutionStatus.DeadLettered;
            }

            if (MessageTypeToStatusMap.TryGetValue(message.MessageType, out var status))
            {
                return status;
            }

            throw new ArgumentException($"Unexpected {nameof(MessageType)}", nameof(message.MessageType));
        }
    }
}
