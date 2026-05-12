using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NimBus.Core.Diagnostics;

namespace NimBus.Core.Messages
{

    public class ResponseService : IResponseService
    {
        private readonly ISender _sender;

        public ResponseService(ISender sender)
        {
            _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        }

        public async Task SendResolutionResponse(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            IMessage response = CreateResponse(messageContext, MessageType.ResolutionResponse, responseContent: messageContext.MessageContent);
            await _sender.Send(response, cancellationToken: cancellationToken);
        }

        public async Task SendSkipResponse(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            IMessage response = CreateResponse(messageContext, MessageType.SkipResponse, responseContent: new MessageContent());
            await _sender.Send(response, cancellationToken: cancellationToken);
        }

        public async Task SendErrorResponse(IMessageContext messageContext, Exception exception, CancellationToken cancellationToken = default)
        {
            IMessage response = CreateResponse(messageContext, MessageType.ErrorResponse, CreateErrorContent(exception, messageContext));
            await _sender.Send(response, cancellationToken: cancellationToken);
        }

        public async Task SendDeadLetterResponse(IMessageContext messageContext, string reason, Exception exception, CancellationToken cancellationToken = default)
        {
            // Routed to the Resolver as an ErrorResponse with the dead-letter properties
            // populated. The Resolver short-circuits on DeadLetterErrorDescription and
            // classifies the audit record as DeadLettered regardless of MessageType.
            var content = exception != null
                ? CreateErrorContent(exception, messageContext)
                : new MessageContent { EventContent = messageContext.MessageContent?.EventContent };
            var response = (Message)CreateResponse(messageContext, MessageType.ErrorResponse, content);
            response.DeadLetterReason = reason;
            response.DeadLetterErrorDescription = FormatDeadLetterDescription(exception, reason);
            await _sender.Send(response, cancellationToken: cancellationToken);
        }

        private static string FormatDeadLetterDescription(Exception exception, string reason) =>
            exception?.ToString() ?? reason;

        public async Task SendDeferralResponse(IMessageContext messageContext, SessionBlockedException exception, CancellationToken cancellationToken = default)
        {
            IMessage response = CreateResponse(messageContext, MessageType.DeferralResponse, CreateErrorContent(exception, messageContext));
            await _sender.Send(response, cancellationToken: cancellationToken);
        }

        public async Task SendPendingHandoffResponse(IMessageContext messageContext, HandoffMetadata handoff, CancellationToken cancellationToken = default)
        {
            var response = (Message)CreateResponse(messageContext, MessageType.PendingHandoffResponse, messageContext.MessageContent);
            response.HandoffReason = handoff?.Reason;
            response.ExternalJobId = handoff?.ExternalJobId;
            response.ExpectedBy = handoff?.ExpectedBy.HasValue == true
                ? DateTime.UtcNow.Add(handoff.ExpectedBy.Value)
                : (DateTime?)null;
            await _sender.Send(response, cancellationToken: cancellationToken);
        }

        public async Task SendRetryResponse(IMessageContext messageContext, int messageDelayMinutes, CancellationToken cancellationToken = default)
        {
            IMessage response = CreateRetryResponse(messageContext, MessageType.RetryRequest, responseContent: messageContext.MessageContent);
            await _sender.Send(response, messageDelayMinutes, cancellationToken);
        }

        public async Task SendContinuationRequestToSelf(IMessageContext deferredMessageContext, CancellationToken cancellationToken = default)
        {
            await _sender.Send(new Message()
            {
                To = Constants.ContinuationId,
                CorrelationId = deferredMessageContext.MessageId,
                SessionId = deferredMessageContext.SessionId,
                EventId = deferredMessageContext.EventId,
                OriginatingMessageId = !deferredMessageContext.OriginatingMessageId.Equals(Constants.Self, StringComparison.OrdinalIgnoreCase) ? deferredMessageContext.OriginatingMessageId : deferredMessageContext.MessageId,
                ParentMessageId = deferredMessageContext.MessageId,
                EventTypeId = deferredMessageContext.EventTypeId,
                MessageType = MessageType.ContinuationRequest,
                MessageContent = new MessageContent(),
            }, cancellationToken: cancellationToken);
        }

        public async Task SendUnsupportedResponse(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            IMessage response = CreateResponse(messageContext, MessageType.UnsupportedResponse, responseContent: messageContext.MessageContent);
            await _sender.Send(response, cancellationToken: cancellationToken);
        }

        private IMessage CreateResponse(IMessageContext messageContext, MessageType responseType, MessageContent responseContent) =>
            new Message()
            {
                To = Constants.ResolverId,
                CorrelationId = messageContext.MessageId,
                SessionId = messageContext.SessionId,
                EventId = messageContext.EventId,
                OriginatingMessageId = !messageContext.OriginatingMessageId.Equals(Constants.Self, StringComparison.OrdinalIgnoreCase) ? messageContext.OriginatingMessageId : messageContext.MessageId,
                ParentMessageId = messageContext.MessageId,
                RetryCount = messageContext.RetryCount ?? null,
                OriginatingFrom = messageContext.From,
                EventTypeId = messageContext.EventTypeId,
                MessageType = responseType,
                MessageContent = responseContent,
                // Carry per-message timings to the Resolver so it can persist them
                // on the audit doc — the message detail page renders them.
                QueueTimeMs = messageContext.QueueTimeMs,
                ProcessingTimeMs = ComputeProcessingTimeMs(messageContext),
            };

        // The terminal handler calls SendResolutionResponse INSIDE the pipeline,
        // before any post-await middleware can finalise ProcessingTimeMs. Prefer
        // an explicitly-set value (used by tests / unusual flows); otherwise
        // compute from HandlerStartedAtUtc captured by ServiceBusAdapter at the
        // receive boundary.
        private static long? ComputeProcessingTimeMs(IMessageContext messageContext)
        {
            if (messageContext.ProcessingTimeMs.HasValue)
                return messageContext.ProcessingTimeMs;
            if (messageContext.HandlerStartedAtUtc is { } start)
                return Math.Max(0, (long)(DateTime.UtcNow - start).TotalMilliseconds);
            return null;
        }


        private IMessage CreateRetryResponse(IMessageContext messageContext, MessageType responseType, MessageContent responseContent) =>
            new Message()
            {
                To = Constants.RetryId,
                CorrelationId = messageContext.MessageId,
                SessionId = messageContext.SessionId,
                EventId = messageContext.EventId,
                OriginatingMessageId = !messageContext.OriginatingMessageId.Equals(Constants.Self, StringComparison.OrdinalIgnoreCase) ? messageContext.OriginatingMessageId : messageContext.MessageId,
                ParentMessageId = messageContext.MessageId,
                RetryCount = messageContext.RetryCount.HasValue ? messageContext.RetryCount + 1 : 1,
                OriginatingFrom = messageContext.From,
                EventTypeId = messageContext.EventTypeId,
                MessageType = MessageType.RetryRequest,
                MessageContent = responseContent,
            };


        private static MessageContent CreateErrorContent(Exception exception, IMessageContext messageContext) =>
            new MessageContent()
            {
                ErrorContent = new ErrorContent()
                {
                    ErrorText = exception.Message,
                    ErrorType = exception.GetType().FullName,
                    ExceptionStackTrace = null,
                    ExceptionSource = null,
                },
                EventContent = messageContext.MessageContent.EventContent
            };

        public async Task SendToDeferredSubscription(IMessageContext messageContext, int deferralSequence, CancellationToken cancellationToken = default)
        {
            IMessage deferredMessage = new Message()
            {
                To = Constants.DeferredSubscriptionName,
                // Preserve the publisher endpoint name. When this parked message is later
                // republished by DeferredMessageProcessor and the receiver picks it back up,
                // StrictMessageHandler reads messageContext.From and throws InvalidMessageException
                // if the property is missing.
                From = messageContext.From,
                CorrelationId = messageContext.CorrelationId,
                SessionId = messageContext.SessionId,           // Session-enabled deferred subscription
                EventId = messageContext.EventId,
                OriginatingMessageId = !messageContext.OriginatingMessageId.Equals(Constants.Self, StringComparison.OrdinalIgnoreCase) ? messageContext.OriginatingMessageId : messageContext.MessageId,
                ParentMessageId = messageContext.MessageId,
                RetryCount = messageContext.RetryCount ?? null,
                OriginatingFrom = messageContext.From,
                EventTypeId = messageContext.EventTypeId,
                MessageType = messageContext.MessageType,
                MessageContent = messageContext.MessageContent,
                OriginalSessionId = messageContext.SessionId,   // Kept for backward compatibility
                DeferralSequence = deferralSequence,
            };

            var endpoint = messageContext.To;
            using var activity = NimBusActivitySources.DeferredProcessor.StartActivity(
                "NimBus.DeferredProcessor.Park", ActivityKind.Internal);
            if (activity is not null)
            {
                if (!string.IsNullOrEmpty(endpoint))
                    activity.SetTag(MessagingAttributes.NimBusEndpoint, endpoint);
                if (!string.IsNullOrEmpty(messageContext.SessionId))
                    activity.SetTag(MessagingAttributes.NimBusSessionKey, messageContext.SessionId);
                if (!string.IsNullOrEmpty(messageContext.EventTypeId))
                    activity.SetTag(MessagingAttributes.NimBusEventType, messageContext.EventTypeId);
            }

            try
            {
                await _sender.Send(deferredMessage, cancellationToken: cancellationToken);
                activity?.SetStatus(ActivityStatusCode.Ok);
                NimBusMeters.DeferredParked.Add(1, BuildEndpointTag(endpoint));
            }
            catch (Exception ex)
            {
                if (activity is not null)
                {
                    activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                    activity.SetTag(MessagingAttributes.ErrorType, ex.GetType().FullName);
                }
                throw;
            }
        }

        private static KeyValuePair<string, object?>[] BuildEndpointTag(string? endpoint)
        {
            if (string.IsNullOrEmpty(endpoint))
                return Array.Empty<KeyValuePair<string, object?>>();
            return new[] { new KeyValuePair<string, object?>(MessagingAttributes.NimBusEndpoint, endpoint) };
        }

        public async Task SendProcessDeferredRequest(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            await _sender.Send(new Message()
            {
                To = Constants.DeferredProcessorId,
                CorrelationId = messageContext.MessageId,
                SessionId = messageContext.SessionId,
                EventId = messageContext.EventId,
                OriginatingMessageId = !messageContext.OriginatingMessageId.Equals(Constants.Self, StringComparison.OrdinalIgnoreCase) ? messageContext.OriginatingMessageId : messageContext.MessageId,
                ParentMessageId = messageContext.MessageId,
                EventTypeId = messageContext.EventTypeId,
                MessageType = MessageType.ProcessDeferredRequest,
                MessageContent = new MessageContent(),
            }, cancellationToken: cancellationToken);
        }
    }
}
