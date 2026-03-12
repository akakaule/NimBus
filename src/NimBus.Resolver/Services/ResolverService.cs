using NimBus.Core.Logging;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.MessageStore;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Broker.Services
{
    public class ResolverService : IMessageHandler
    {
        private readonly ICosmosDbClient _cosmosClient;
        private readonly ILoggerProvider _loggerProvider;

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
            [MessageType.ErrorResponse] = ResolutionStatus.Failed,
            [MessageType.ResolutionResponse] = ResolutionStatus.Completed,
            [MessageType.DeferralResponse] = ResolutionStatus.Deferred,
            [MessageType.SkipResponse] = ResolutionStatus.Skipped,
            [MessageType.UnsupportedResponse] = ResolutionStatus.Unsupported,
        };

        public ResolverService(ILoggerProvider loggerProvider, ICosmosDbClient cosmosClient)
        {
            _loggerProvider = loggerProvider;
            _cosmosClient = cosmosClient;
        }

        public async Task Handle(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            ILogger logger = _loggerProvider.GetContextualLogger(messageContext);
            logger.Verbose("Resolver: Handle {EventTypeId} EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
                messageContext.MessageContent.EventContent?.EventTypeId, messageContext.EventId, messageContext.MessageId, messageContext.SessionId);

            try
            {
                MessageEntity messageEntity = await CreateMessageEntity(messageContext, logger);

                await _cosmosClient.StoreMessage(messageEntity);

                var status = await UpdateState(messageEntity, logger);

                logger.Information("Resolver: Updated Endpoint EndpointId:{EndpointId}, Status:{Status}, EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
                    messageEntity.EndpointId, status, messageEntity.EventId, messageContext.MessageId, messageEntity.SessionId);
                await messageContext.Complete(cancellationToken);
            }
            catch (RequestLimitException ex)
            {
                await HandleThrottling(messageContext, ex.RetryAfter, logger, cancellationToken);
            }
            catch (TransientException transientException)
            {
                logger.Error(transientException, "Resolver: Transient exception EventId:{EventId}", messageContext.EventId);
                await messageContext.Abandon(transientException);
            }
            catch (Exception unexpectedException)
            {
                logger.Error(unexpectedException, "Resolver: Failed to handle message, add to DeadLetter. EventId:{EventId}", messageContext.EventId);
                await messageContext.DeadLetter("Failed to handle message.", unexpectedException, cancellationToken);
            }
        }

        private async Task HandleThrottling(IMessageContext messageContext, TimeSpan? retryAfter, ILogger logger, CancellationToken cancellationToken)
        {
            var retryCount = messageContext.ThrottleRetryCount;

            if (retryCount >= MaxThrottleRetries)
            {
                logger.Error("Resolver: Max throttle retries ({MaxRetries}) exceeded. DeadLettering. EventId:{EventId}, SessionId:{SessionId}",
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

            logger.Verbose(
                "Resolver: Throttle delay decision - using {DelaySource}. CosmosRetryAfter:{CosmosRetryAfter}s, CalculatedBackoff:{CalculatedBackoff}s, EventId:{EventId}",
                useCosmosRetryAfter ? "CosmosRetryAfter" : "CalculatedBackoff",
                retryAfter?.TotalSeconds,
                calculatedDelay.TotalSeconds,
                messageContext.EventId);

            logger.Information(
                "Resolver: Cosmos DB throttled. Scheduling redelivery in {DelaySeconds}s. EventId:{EventId}, SessionId:{SessionId}, RetryCount:{RetryCount}/{MaxRetries}",
                delay.TotalSeconds, messageContext.EventId, messageContext.SessionId, retryCount + 1, MaxThrottleRetries);

            try
            {
                await messageContext.ScheduleRedelivery(delay, retryCount + 1, cancellationToken);
            }
            catch (TransientException ex)
            {
                logger.Information(ex, "Resolver: Failed to schedule redelivery. Abandoning for retry. EventId:{EventId}, SessionId:{SessionId}",
                    messageContext.EventId, messageContext.SessionId);
                await messageContext.Abandon(ex);
            }
        }

        private async Task<MessageEntity> CreateMessageEntity(IReceivedMessage message, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(message);

            if (message.MessageType == MessageType.RetryRequest)
            {
                var messageAudit = new MessageAuditEntity() { AuditorName = Constants.ManagerId, AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Retry };
                await _cosmosClient.StoreMessageAudit(message.EventId, messageAudit);
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
                     message.MessageType == MessageType.SkipRequest)
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

        private UnresolvedEvent CreateUnresolvedEvent(MessageEntity message)
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
            };
        }

        private async Task<ResolutionStatus> UpdateState(MessageEntity message, ILogger logger)
        {
            ResolutionStatus status = GetResultingStatus(message);
            UnresolvedEvent unresolvedEvent = CreateUnresolvedEvent(message);

            var statusHandlers = new Dictionary<ResolutionStatus, Func<Task>>
            {
                [ResolutionStatus.Completed] = () => _cosmosClient.UploadCompletedMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
                [ResolutionStatus.Skipped] = () => _cosmosClient.UploadSkippedMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
                [ResolutionStatus.Failed] = () => _cosmosClient.UploadFailedMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
                [ResolutionStatus.Deferred] = () => _cosmosClient.UploadDeferredMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
                [ResolutionStatus.Pending] = () => _cosmosClient.UploadPendingMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
                [ResolutionStatus.DeadLettered] = () => _cosmosClient.UploadDeadletteredMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
                [ResolutionStatus.Unsupported] = () => _cosmosClient.UploadUnsupportedMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
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
