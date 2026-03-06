using NimBus.Core.Logging;
using NimBus.Core.Messages.Exceptions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Messages
{
    public class MessageHandler : IMessageHandler
    {
        private readonly ILoggerProvider _loggerProvider;

        public MessageHandler(ILoggerProvider loggerProvider)
        {
            _loggerProvider = loggerProvider;
        }

        public async Task Handle(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            ILogger logger = _loggerProvider.GetContextualLogger(messageContext);

            try
            {
                await HandleByMessageType(messageContext, logger, cancellationToken);
            }
            catch (TransientException transientException)
            {
                logger.Error(transientException?.InnerException, $"Transient Error. Failed to handle message. EventId:{messageContext?.EventId}, MessageId:{messageContext.MessageId}, SessionId:{messageContext.SessionId}");

                try
                {
                    await messageContext.Abandon(transientException);
                }
                catch (Exception ex)
                {
                    logger.Error(ex, $"Failed to abandon message. EventId:{messageContext?.EventId}, MessageId:{messageContext.MessageId}, SessionId:{messageContext.SessionId}");
                }
            }
            catch (SessionBlockedException)
            {
            }
            catch (EventContextHandlerException)
            {
            }
            catch (Exception unexpectedException)
            {
                try
                {
                    logger.Error(unexpectedException, $"Unexpected Error. Failed to handle message. EventId:{messageContext?.EventId}, MessageId:{messageContext.MessageId}, SessionId:{messageContext.SessionId}");
                    await messageContext.DeadLetter("Failed to handle message.", unexpectedException, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.Error(ex, $"Failed to deadletter message. EventId:{messageContext?.EventId}, MessageId:{messageContext.MessageId}, SessionId:{messageContext.SessionId}");
                }
            }
        }

        private Task HandleByMessageType(IMessageContext messageContext, ILogger logger, CancellationToken cancellationToken)
        {
            switch (messageContext.MessageType)
            {
                case MessageType.EventRequest:
                case MessageType.UnsupportedResponse:
                    return HandleEventRequest(messageContext, logger, cancellationToken);

                case MessageType.ContinuationRequest:
                    return HandleContinuationRequest(messageContext, logger, cancellationToken);

                case MessageType.SkipRequest:
                    return HandleSkipRequest(messageContext, logger, cancellationToken);

                case MessageType.ResubmissionRequest:
                    return HandleResubmissionRequest(messageContext, logger, cancellationToken);

                case MessageType.RetryRequest:
                    return HandleRetryRequest(messageContext, logger, cancellationToken);

                case MessageType.ProcessDeferredRequest:
                    return HandleProcessDeferredRequest(messageContext, logger, cancellationToken);

                case MessageType.ErrorResponse:
                    return HandleErrorResponse(messageContext, logger, cancellationToken);

                case MessageType.ResolutionResponse:
                    return HandleResolutionResponse(messageContext, logger, cancellationToken);

                case MessageType.DeferralResponse:
                    return HandleDeferralResponse(messageContext, logger, cancellationToken);

                default:
                    return HandleDefault(messageContext, logger, cancellationToken);
            }
        }

        public virtual Task HandleDefault(IMessageContext messageContext, ILogger logger, CancellationToken cancellationToken = default) =>
            throw new UnsupportedMessageTypeException(messageContext.MessageType);

        public virtual Task HandleDeferralResponse(IMessageContext messageContext, ILogger logger, CancellationToken cancellationToken = default) =>
            HandleDefault(messageContext, logger, cancellationToken);

        public virtual Task HandleResolutionResponse(IMessageContext messageContext, ILogger logger, CancellationToken cancellationToken = default) =>
            HandleDefault(messageContext, logger, cancellationToken);

        public virtual Task HandleErrorResponse(IMessageContext messageContext, ILogger logger, CancellationToken cancellationToken = default) =>
            HandleDefault(messageContext, logger, cancellationToken);

        public virtual Task HandleResubmissionRequest(IMessageContext messageContext, ILogger logger, CancellationToken cancellationToken = default) =>
            HandleDefault(messageContext, logger, cancellationToken);

        public virtual Task HandleRetryRequest(IMessageContext messageContext, ILogger logger, CancellationToken cancellationToken = default) =>
            HandleDefault(messageContext, logger, cancellationToken);

        public virtual Task HandleSkipRequest(IMessageContext messageContext, ILogger logger, CancellationToken cancellationToken = default) =>
            HandleDefault(messageContext, logger, cancellationToken);

        public virtual Task HandleContinuationRequest(IMessageContext messageContext, ILogger logger, CancellationToken cancellationToken = default) =>
            HandleDefault(messageContext, logger, cancellationToken);

        public virtual Task HandleEventRequest(IMessageContext messageContext, ILogger logger, CancellationToken cancellationToken = default) =>
            HandleDefault(messageContext, logger, cancellationToken);

        public virtual Task HandleProcessDeferredRequest(IMessageContext messageContext, ILogger logger, CancellationToken cancellationToken = default) =>
            HandleDefault(messageContext, logger, cancellationToken);
    }
}
