using NimBus.Core.Extensions;
using NimBus.Core.Messages.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Messages
{
    public class StrictMessageHandler : MessageHandler
    {
        private readonly IEventContextHandler _eventContextHandler;
        private readonly IResponseService _responseService;
        private readonly IRetryPolicyProvider _retryPolicyProvider;
        private readonly ILogger _logger;

        public StrictMessageHandler(IEventContextHandler eventContextHandler, IResponseService responseService, ILogger logger = null)
            : base(logger ?? NullLogger.Instance)
        {
            _eventContextHandler = eventContextHandler;
            _responseService = responseService;
            _logger = logger ?? NullLogger.Instance;
        }

        public StrictMessageHandler(
            IEventContextHandler eventContextHandler,
            IResponseService responseService,
            ILogger logger,
            IRetryPolicyProvider retryPolicyProvider) : base(logger ?? NullLogger.Instance)
        {
            _eventContextHandler = eventContextHandler;
            _responseService = responseService;
            _retryPolicyProvider = retryPolicyProvider;
            _logger = logger ?? NullLogger.Instance;
        }

        public StrictMessageHandler(
            IEventContextHandler eventContextHandler,
            IResponseService responseService,
            ILogger logger,
            IRetryPolicyProvider retryPolicyProvider,
            MessagePipeline pipeline,
            MessageLifecycleNotifier lifecycleNotifier) : base(logger ?? NullLogger.Instance, pipeline, lifecycleNotifier)
        {
            _eventContextHandler = eventContextHandler;
            _responseService = responseService;
            _retryPolicyProvider = retryPolicyProvider;
            _logger = logger ?? NullLogger.Instance;
        }

        public override async Task HandleEventRequest(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            try
            {
                LogInfo(messageContext, "Handle");

                if (messageContext.EventTypeId == "Heartbeat" && messageContext.MessageType == MessageType.EventRequest)
                {
                    await SendResolutionResponse(messageContext, cancellationToken);
                }
                else
                {
                    await VerifySessionIsNotBlocked(messageContext, cancellationToken);
                    await HandleEventContent(messageContext, cancellationToken);
                    await SendResolutionResponse(messageContext, cancellationToken);
                }
                await CompleteMessage(messageContext, cancellationToken);
                LogInfo(messageContext, "Successfully processed");
            }
            catch (EventHandlerNotFoundException exception)
            {
                LogError(messageContext, "Failed to handle event", exception);
                await SendUnsupportedResponse(messageContext, cancellationToken);
                await CompleteMessage(messageContext, cancellationToken);
            }
            catch (SessionBlockedException exception)
            {
                LogError(messageContext, "Failed to handle event", exception);
                await SendDeferralResponse(messageContext, exception, cancellationToken);
                await DeferMessageToSubscription(messageContext, cancellationToken);
                throw;
            }
            catch (EventContextHandlerException exception)
            {
                LogError(messageContext, "Failed to handle event", exception);
                await SendErrorResponse(messageContext, exception, cancellationToken);
                await BlockSession(messageContext, cancellationToken);
                await CompleteMessage(messageContext, cancellationToken);
                await CheckForRetry(messageContext, exception, cancellationToken);
                throw;
            }
        }

        public override async Task HandleRetryRequest(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            try
            {
                LogInfo(messageContext, "Handle (RetryRequest)");
                await VerifySessionIsBlockedByThis(messageContext, cancellationToken);
                await HandleEventContent(messageContext, cancellationToken);
                await UnblockSession(messageContext, cancellationToken);
                await ContinueWithAnyDeferredMessages(messageContext, cancellationToken);
                await SendResolutionResponse(messageContext, cancellationToken);
                await CompleteMessage(messageContext, cancellationToken);
                LogInfo(messageContext, "Successfully processed (RetryRequest)");
            }
            catch (SessionBlockedException exception)
            {
                LogError(messageContext, "Failed to handle event (RetryRequest)", exception);
                await SendResolutionResponse(messageContext, cancellationToken);
                await CompleteMessage(messageContext, cancellationToken);
            }
            catch (EventContextHandlerException exception)
            {
                LogError(messageContext, "Failed to handle event (RetryRequest)", exception);
                await SendErrorResponse(messageContext, exception, cancellationToken);
                await CompleteMessage(messageContext, cancellationToken);
                await CheckForRetry(messageContext, exception, cancellationToken);
            }
        }

        public override async Task HandleResubmissionRequest(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            try
            {
                LogInfo(messageContext, "Handle (Resubmission)");
                AuthorizeManagerRequest(messageContext);
                await HandleEventContent(messageContext, cancellationToken);
                if (await messageContext.IsSessionBlockedByThis(cancellationToken))
                    await UnblockSession(messageContext, cancellationToken);
                await ContinueWithAnyDeferredMessages(messageContext, cancellationToken);
                await SendResolutionResponse(messageContext, cancellationToken);
                await CompleteMessage(messageContext, cancellationToken);
                LogInfo(messageContext, "Successfully processed (Resubmission)");
            }
            catch (EventHandlerNotFoundException exception)
            {
                LogError(messageContext, "Failed to handle event (Resubmission)", exception);
                await SendUnsupportedResponse(messageContext, cancellationToken);
                await CompleteMessage(messageContext, cancellationToken);
            }
            catch (EventContextHandlerException exception)
            {
                LogError(messageContext, "Failed to handle event (Resubmission)", exception);
                await SendErrorResponse(messageContext, exception, cancellationToken);
                await CompleteMessage(messageContext, cancellationToken);
            }
        }

        public override async Task HandleSkipRequest(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            try
            {
                LogInfo(messageContext, "Handle (Skip)");
                AuthorizeManagerRequest(messageContext);
                await VerifySessionIsBlockedByThis(messageContext, cancellationToken);
                await UnblockSession(messageContext, cancellationToken);
                await ContinueWithAnyDeferredMessages(messageContext, cancellationToken);
                await SendSkipResponse(messageContext, cancellationToken);
                await CompleteMessage(messageContext, cancellationToken);
            }
            catch (SessionBlockedException)
            {
                await SendSkipResponse(messageContext, cancellationToken);
                await CompleteMessage(messageContext, cancellationToken);
            }

            LogInfo(messageContext, "Successfully processed (Skip)");
        }

        public override async Task HandleContinuationRequest(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            try
            {
                LogInfo(messageContext, "Handle (Continuation)");

                AuthorizeContinuationRequest(messageContext);
                IMessageContext deferredMessageContext = await ReceiveNextDeferredAndVerifyEventId(messageContext, true, cancellationToken);
                await HandleEventRequest(deferredMessageContext, cancellationToken);
                await ContinueWithAnyDeferredMessages(messageContext, cancellationToken);
                await CompleteMessage(messageContext, cancellationToken);
            }
            catch (EventContextHandlerException)
            {
                await CompleteMessage(messageContext, cancellationToken);
            }
            catch (NextDeferredException)
            {
                await CompleteMessage(messageContext, cancellationToken);
            }

            LogInfo(messageContext, "Successfully processed (Continuation)");
        }

        // HandleProcessDeferredRequest is intentionally NOT overridden here.
        // Deferred message processing is handled by a separate DeferredProcessorFunction
        // in each subscriber app, not by the core message handler.

        private Task CompleteMessage(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            messageContext.Complete(cancellationToken);

        private Task DeferMessage(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            messageContext.Defer(cancellationToken);

        private async Task DeferMessageToSubscription(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            int deferralSequence = await messageContext.GetNextDeferralSequenceAndIncrement(cancellationToken);
            await _responseService.SendToDeferredSubscription(messageContext, deferralSequence, cancellationToken);
            await messageContext.IncrementDeferredCount(cancellationToken);
            await messageContext.Complete(cancellationToken);
        }

        private Task SendResolutionResponse(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            _responseService.SendResolutionResponse(messageContext, cancellationToken);

        private Task SendSkipResponse(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            _responseService.SendSkipResponse(messageContext, cancellationToken);

        private Task SendErrorResponse(IMessageContext messageContext, EventContextHandlerException exception, CancellationToken cancellationToken = default) =>
            _responseService.SendErrorResponse(messageContext, exception, cancellationToken);

        private Task SendDeferralResponse(IMessageContext messageContext, SessionBlockedException exception, CancellationToken cancellationToken = default) =>
            _responseService.SendDeferralResponse(messageContext, exception, cancellationToken);

        private Task BlockSession(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            messageContext.BlockSession(cancellationToken);

        private Task UnblockSession(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            messageContext.UnblockSession(cancellationToken);

        private Task SendRetryResponse(IMessageContext messageContext, int messageDelayMinutes, CancellationToken cancellationToken = default) =>
            _responseService.SendRetryResponse(messageContext, messageDelayMinutes, cancellationToken);

        private Task SendUnsupportedResponse(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            _responseService.SendUnsupportedResponse(messageContext, cancellationToken);

        private async Task<IMessageContext> ReceiveNextDeferredAndVerifyEventId(IMessageContext messageContext, bool removeFromQueue = false, CancellationToken cancellationToken = default)
        {
            IMessageContext nextDeferred;
            if (removeFromQueue)
            {
                nextDeferred = await messageContext.ReceiveNextDeferredWithPop(cancellationToken);
            }
            else
            {
                nextDeferred = await messageContext.ReceiveNextDeferred(cancellationToken);
            }

            if (!messageContext.EventId.Equals(nextDeferred?.EventId, StringComparison.OrdinalIgnoreCase))
                throw new NextDeferredException($"Unable to continue with {messageContext.EventId}, because it is not the next deferred event request in this session.");
            return nextDeferred;
        }

        private void AuthorizeManagerRequest(IMessageContext messageContext)
        {
            if (!messageContext.From.Equals(Constants.ManagerId, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException($"Only {Constants.ManagerId} is authorized to send {messageContext.MessageType} messages.");
        }

        private void AuthorizeContinuationRequest(IMessageContext messageContext)
        {
            if (!messageContext.From.Equals(Constants.ContinuationId, StringComparison.OrdinalIgnoreCase) &&
                !messageContext.From.Equals(Constants.ManagerId, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException($"{messageContext.From} is not authorized to send {MessageType.ContinuationRequest} messages to this messaging entity.");
        }

        private async Task VerifySessionIsBlockedByThis(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            if (!await messageContext.IsSessionBlockedByThis(cancellationToken))
            {
                var blockedBy = await messageContext.GetBlockedByEventId(cancellationToken);
                throw new SessionBlockedException($"Session {messageContext.SessionId} is blocked by {blockedBy}");
            }
        }

        private async Task VerifySessionIsNotBlocked(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            var blockedBy = await messageContext.GetBlockedByEventId(cancellationToken);
            if (!string.IsNullOrEmpty(blockedBy))
                throw new SessionBlockedException($"Session {messageContext.SessionId} is blocked by {blockedBy}");
        }

        private async Task HandleEventContent(IMessageContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                await _eventContextHandler.Handle(context, cancellationToken);
            }
            catch (TransientException)
            {
                throw;
            }
            catch (EventHandlerNotFoundException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new EventContextHandlerException(exception)
                {
                    Source = exception.Source
                };
            }
        }

        private async Task ContinueWithAnyDeferredMessages(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            var next = await messageContext.ReceiveNextDeferred(cancellationToken);
            if (next != null)
            {
                await _responseService.SendContinuationRequestToSelf(next, cancellationToken);
                LogInfo(messageContext, "Send ContinuationRequest (legacy)");
                return;
            }

            var deferredCount = await messageContext.GetDeferredCount(cancellationToken);
            if (deferredCount > 0)
            {
                await _responseService.SendProcessDeferredRequest(messageContext, cancellationToken);
                LogInfo(messageContext, $"Send ProcessDeferredRequest ({deferredCount} deferred messages)");
            }
        }

        private async Task CheckForRetry(IMessageContext messageContext, EventContextHandlerException exception, CancellationToken cancellationToken = default)
        {
            var eventTypeId = messageContext.MessageContent.EventContent.EventTypeId;
            var exceptionText = $"{exception?.InnerException} {exception}";
            var retryCount = messageContext.RetryCount ?? 0;

            if (_retryPolicyProvider != null)
            {
                var policy = _retryPolicyProvider.GetRetryPolicy(eventTypeId, exceptionText, messageContext.To);
                if (policy != null && retryCount < policy.MaxRetries)
                {
                    var delayMinutes = policy.GetDelayMinutes(retryCount);
                    await SendRetryResponse(messageContext, delayMinutes, cancellationToken);
                }
                return;
            }

#pragma warning disable CS0618
            var retryDefinition = RetryDefinitions.GetRetryDefinition(eventTypeId, exceptionText, messageContext.To);
            if (retryDefinition != null && messageContext.RetryCount != null && messageContext.RetryCount < retryDefinition.RetryCount)
            {
                await SendRetryResponse(messageContext, retryDefinition.RetryDelay, cancellationToken);
            }
#pragma warning restore CS0618
        }

        private void LogInfo(IMessageContext messageContext, string prefixMessage)
        {
            _logger.LogInformation("{Prefix} EventTypeId:{EventTypeId}, EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
                prefixMessage, messageContext.MessageContent?.EventContent?.EventTypeId, messageContext.EventId, messageContext.MessageId, messageContext.SessionId);
        }

        private void LogError(IMessageContext messageContext, string prefixMessage, Exception exception)
        {
            _logger.LogError(exception, "{Prefix} EventTypeId:{EventTypeId}, EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
                prefixMessage, messageContext.MessageContent?.EventContent?.EventTypeId, messageContext.EventId, messageContext.MessageId, messageContext.SessionId);
        }
    }
}
