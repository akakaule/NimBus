using NimBus.Core.Extensions;
using NimBus.Core.Messages.Exceptions;
using NimBus.Core.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Messages
{
    public class MessageHandler : IMessageHandler
    {
        private readonly ILogger _logger;
        private readonly MessagePipeline _pipeline;
        private readonly MessageLifecycleNotifier _lifecycleNotifier;

        public MessageHandler(ILogger logger)
            : this(logger, null, null)
        {
        }

        public MessageHandler(ILogger logger, MessagePipeline pipeline, MessageLifecycleNotifier lifecycleNotifier)
        {
            _logger = logger ?? NullLogger.Instance;
            _pipeline = pipeline;
            _lifecycleNotifier = lifecycleNotifier;
        }

        public async Task Handle(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            if (_lifecycleNotifier?.HasObservers == true)
            {
                await _lifecycleNotifier.NotifyReceived(messageContext, cancellationToken);
            }

            try
            {
                if (_pipeline?.HasBehaviors == true)
                {
                    await _pipeline.Execute(
                        messageContext,
                        (ctx, ct) => HandleByMessageType(ctx, ct),
                        cancellationToken);
                }
                else
                {
                    await HandleByMessageType(messageContext, cancellationToken);
                }

                if (_lifecycleNotifier?.HasObservers == true)
                {
                    await _lifecycleNotifier.NotifyCompleted(messageContext, cancellationToken);
                }
            }
            catch (TransientException transientException)
            {
                _logger.LogError(transientException?.InnerException, "Transient Error. Failed to handle message. EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
                    messageContext?.EventId, messageContext.MessageId, messageContext.SessionId);

                if (_lifecycleNotifier?.HasObservers == true)
                {
                    await _lifecycleNotifier.NotifyFailed(messageContext, transientException, cancellationToken);
                }

                try
                {
                    await messageContext.Abandon(transientException);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to abandon message. EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
                        messageContext?.EventId, messageContext.MessageId, messageContext.SessionId);
                }
            }
            catch (SessionBlockedException)
            {
            }
            catch (EventContextHandlerException)
            {
            }
            catch (MessageAlreadyDeadLetteredException alreadyDeadLettered)
            {
                // Message was already dead-lettered by middleware (e.g., ValidationMiddleware).
                // Fire lifecycle notifications without attempting to dead-letter again.
                _logger.LogWarning("Message already dead-lettered by middleware. EventId:{EventId}, Reason:{Reason}",
                    messageContext?.EventId, alreadyDeadLettered.Message);

                if (_lifecycleNotifier?.HasObservers == true)
                {
                    await _lifecycleNotifier.NotifyFailed(messageContext, alreadyDeadLettered, cancellationToken);
                    await _lifecycleNotifier.NotifyDeadLettered(messageContext, alreadyDeadLettered.Message, alreadyDeadLettered, cancellationToken);
                }
            }
            catch (Exception unexpectedException)
            {
                if (_lifecycleNotifier?.HasObservers == true)
                {
                    await _lifecycleNotifier.NotifyFailed(messageContext, unexpectedException, cancellationToken);
                }

                try
                {
                    _logger.LogError(unexpectedException, "Unexpected Error. Failed to handle message. EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
                        messageContext?.EventId, messageContext.MessageId, messageContext.SessionId);
                    await messageContext.DeadLetter("Failed to handle message.", unexpectedException, cancellationToken);

                    if (_lifecycleNotifier?.HasObservers == true)
                    {
                        await _lifecycleNotifier.NotifyDeadLettered(messageContext, "Failed to handle message.", unexpectedException, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to deadletter message. EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
                        messageContext?.EventId, messageContext.MessageId, messageContext.SessionId);
                }
            }
        }

        private Task HandleByMessageType(IMessageContext messageContext, CancellationToken cancellationToken)
        {
            switch (messageContext.MessageType)
            {
                case MessageType.EventRequest:
                case MessageType.UnsupportedResponse:
                    return HandleEventRequest(messageContext, cancellationToken);

                case MessageType.ContinuationRequest:
                    return HandleContinuationRequest(messageContext, cancellationToken);

                case MessageType.SkipRequest:
                    return HandleSkipRequest(messageContext, cancellationToken);

                case MessageType.ResubmissionRequest:
                    return HandleResubmissionRequest(messageContext, cancellationToken);

                case MessageType.RetryRequest:
                    return HandleRetryRequest(messageContext, cancellationToken);

                case MessageType.ProcessDeferredRequest:
                    return HandleProcessDeferredRequest(messageContext, cancellationToken);

                case MessageType.ErrorResponse:
                    return HandleErrorResponse(messageContext, cancellationToken);

                case MessageType.ResolutionResponse:
                    return HandleResolutionResponse(messageContext, cancellationToken);

                case MessageType.DeferralResponse:
                    return HandleDeferralResponse(messageContext, cancellationToken);

                default:
                    return HandleDefault(messageContext, cancellationToken);
            }
        }

        public virtual Task HandleDefault(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            throw new UnsupportedMessageTypeException(messageContext.MessageType);

        public virtual Task HandleDeferralResponse(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            HandleDefault(messageContext, cancellationToken);

        public virtual Task HandleResolutionResponse(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            HandleDefault(messageContext, cancellationToken);

        public virtual Task HandleErrorResponse(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            HandleDefault(messageContext, cancellationToken);

        public virtual Task HandleResubmissionRequest(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            HandleDefault(messageContext, cancellationToken);

        public virtual Task HandleRetryRequest(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            HandleDefault(messageContext, cancellationToken);

        public virtual Task HandleSkipRequest(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            HandleDefault(messageContext, cancellationToken);

        public virtual Task HandleContinuationRequest(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            HandleDefault(messageContext, cancellationToken);

        public virtual Task HandleEventRequest(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            HandleDefault(messageContext, cancellationToken);

        public virtual Task HandleProcessDeferredRequest(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            HandleDefault(messageContext, cancellationToken);
    }
}
