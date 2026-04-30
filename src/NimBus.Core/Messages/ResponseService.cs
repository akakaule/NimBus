using System;
using System.Threading;
using System.Threading.Tasks;

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

        public async Task SendDeferralResponse(IMessageContext messageContext, SessionBlockedException exception, CancellationToken cancellationToken = default)
        {
            IMessage response = CreateResponse(messageContext, MessageType.DeferralResponse, CreateErrorContent(exception, messageContext));
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
                ProcessingTimeMs = messageContext.ProcessingTimeMs,
            };


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
            await _sender.Send(deferredMessage, cancellationToken: cancellationToken);
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
