using NimBus.Core.Extensions;
using NimBus.Core.Messages.Exceptions;
using NimBus.MessageStore.Abstractions;
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
        private readonly IRetryPolicyProvider? _retryPolicyProvider;
        private readonly IPermanentFailureClassifier? _permanentFailureClassifier;
        private readonly ISessionStateStore? _sessionStateStore;
        private readonly ILogger _logger;

        public StrictMessageHandler(IEventContextHandler eventContextHandler, IResponseService responseService, ILogger logger = null)
            : this(eventContextHandler, responseService, logger, sessionStateStore: null)
        {
        }

        public StrictMessageHandler(
            IEventContextHandler eventContextHandler,
            IResponseService responseService,
            ILogger logger,
            ISessionStateStore? sessionStateStore)
            : base(logger ?? NullLogger.Instance, pipeline: null, lifecycleNotifier: null, responseService: responseService)
        {
            _eventContextHandler = eventContextHandler;
            _responseService = responseService;
            _sessionStateStore = sessionStateStore;
            _logger = logger ?? NullLogger.Instance;
        }

        public StrictMessageHandler(
            IEventContextHandler eventContextHandler,
            IResponseService responseService,
            ILogger logger,
            IRetryPolicyProvider? retryPolicyProvider)
            : this(eventContextHandler, responseService, logger, retryPolicyProvider, sessionStateStore: null)
        {
        }

        public StrictMessageHandler(
            IEventContextHandler eventContextHandler,
            IResponseService responseService,
            ILogger logger,
            IRetryPolicyProvider? retryPolicyProvider,
            ISessionStateStore? sessionStateStore)
            : base(logger ?? NullLogger.Instance, pipeline: null, lifecycleNotifier: null, responseService: responseService)
        {
            _eventContextHandler = eventContextHandler;
            _responseService = responseService;
            _retryPolicyProvider = retryPolicyProvider;
            _sessionStateStore = sessionStateStore;
            _logger = logger ?? NullLogger.Instance;
        }

        public StrictMessageHandler(
            IEventContextHandler eventContextHandler,
            IResponseService responseService,
            ILogger logger,
            IRetryPolicyProvider? retryPolicyProvider,
            MessagePipeline pipeline,
            MessageLifecycleNotifier lifecycleNotifier,
            IPermanentFailureClassifier permanentFailureClassifier = null,
            ISessionStateStore? sessionStateStore = null) : base(logger ?? NullLogger.Instance, pipeline, lifecycleNotifier, responseService)
        {
            _eventContextHandler = eventContextHandler;
            _responseService = responseService;
            _retryPolicyProvider = retryPolicyProvider;
            _permanentFailureClassifier = permanentFailureClassifier;
            _sessionStateStore = sessionStateStore;
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
                if (await IsSessionBlockedByThis(messageContext, cancellationToken))
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
            int deferralSequence = await GetNextDeferralSequenceAndIncrement(messageContext, cancellationToken);
            await _responseService.SendToDeferredSubscription(messageContext, deferralSequence, cancellationToken);
            await IncrementDeferredCount(messageContext, cancellationToken);
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

        // The session-state helpers below prefer the injected ISessionStateStore so
        // that DI-wired hosts go straight to the store instead of routing through
        // the [Obsolete] IMessageContext bridges. When no store is wired (legacy
        // unit-test paths that construct StrictMessageHandler directly without DI),
        // they fall back to the IMessageContext methods, whose bridge bodies cover
        // the same semantics.

        private Task BlockSession(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            if (_sessionStateStore != null)
                return _sessionStateStore.BlockSession(messageContext.To, messageContext.SessionId, messageContext.EventId, cancellationToken);
#pragma warning disable CS0618 // Bridge fallback for hosts without DI-registered ISessionStateStore.
            return messageContext.BlockSession(cancellationToken);
#pragma warning restore CS0618
        }

        private Task UnblockSession(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            if (_sessionStateStore != null)
                return _sessionStateStore.UnblockSession(messageContext.To, messageContext.SessionId, cancellationToken);
#pragma warning disable CS0618
            return messageContext.UnblockSession(cancellationToken);
#pragma warning restore CS0618
        }

        private Task<bool> IsSessionBlockedByThis(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            if (_sessionStateStore != null)
                return _sessionStateStore.IsSessionBlockedByThis(messageContext.To, messageContext.SessionId, messageContext.EventId, cancellationToken);
#pragma warning disable CS0618
            return messageContext.IsSessionBlockedByThis(cancellationToken);
#pragma warning restore CS0618
        }

        private async Task<string> GetBlockedByEventId(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            if (_sessionStateStore != null)
            {
                var value = await _sessionStateStore.GetBlockedByEventId(messageContext.To, messageContext.SessionId, cancellationToken);
                return string.IsNullOrEmpty(value) ? null : value;
            }
#pragma warning disable CS0618
            return await messageContext.GetBlockedByEventId(cancellationToken);
#pragma warning restore CS0618
        }

        private Task<int> GetNextDeferralSequenceAndIncrement(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            if (_sessionStateStore != null)
                return _sessionStateStore.GetNextDeferralSequenceAndIncrement(messageContext.To, messageContext.SessionId, cancellationToken);
#pragma warning disable CS0618
            return messageContext.GetNextDeferralSequenceAndIncrement(cancellationToken);
#pragma warning restore CS0618
        }

        private Task IncrementDeferredCount(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            if (_sessionStateStore != null)
                return _sessionStateStore.IncrementDeferredCount(messageContext.To, messageContext.SessionId, cancellationToken);
#pragma warning disable CS0618
            return messageContext.IncrementDeferredCount(cancellationToken);
#pragma warning restore CS0618
        }

        private Task<int> GetDeferredCount(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            if (_sessionStateStore != null)
                return _sessionStateStore.GetDeferredCount(messageContext.To, messageContext.SessionId, cancellationToken);
#pragma warning disable CS0618
            return messageContext.GetDeferredCount(cancellationToken);
#pragma warning restore CS0618
        }

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
            if (!await IsSessionBlockedByThis(messageContext, cancellationToken))
            {
                var blockedBy = await GetBlockedByEventId(messageContext, cancellationToken);
                throw new SessionBlockedException($"Session {messageContext.SessionId} is blocked by {blockedBy}");
            }
        }

        private async Task VerifySessionIsNotBlocked(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            var blockedBy = await GetBlockedByEventId(messageContext, cancellationToken);
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
                if (_permanentFailureClassifier?.IsPermanentFailure(exception) == true)
                {
                    throw new PermanentFailureException(exception);
                }

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

            var deferredCount = await GetDeferredCount(messageContext, cancellationToken);
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
