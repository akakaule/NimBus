using NimBus.Core.Extensions;
using NimBus.Core.Inbox;
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
        // Upper bound for the best-effort deferred-sequence restore, which must run even
        // when the caller's token is already cancelled and so cannot inherit its lifetime.
        private static readonly TimeSpan DeferredRestoreTimeout = TimeSpan.FromSeconds(30);

        private readonly IEventContextHandler _eventContextHandler;
        private readonly IResponseService _responseService;
        private readonly IRetryPolicyProvider? _retryPolicyProvider;
#pragma warning disable CS0618
        private readonly IPermanentFailureClassifier? _permanentFailureClassifier;
#pragma warning restore CS0618
        private readonly IFailureDispositionClassifier _failureDispositionClassifier;
        private readonly InboxDuplicateDetector? _inboxDuplicateDetector;
        private readonly ILogger _logger;

        public StrictMessageHandler(IEventContextHandler eventContextHandler, IResponseService responseService, ILogger logger = null)
            : this(
                eventContextHandler,
                responseService,
                logger,
                retryPolicyProvider: null,
                pipeline: null,
                lifecycleNotifier: null,
                permanentFailureClassifier: null,
                failureDispositionClassifier: null)
        {
        }

#pragma warning disable CS0618
        public StrictMessageHandler(
            IEventContextHandler eventContextHandler,
            IResponseService responseService,
            ILogger logger,
            IRetryPolicyProvider? retryPolicyProvider)
            : this(
                eventContextHandler,
                responseService,
                logger,
                retryPolicyProvider,
                pipeline: null,
                lifecycleNotifier: null,
                permanentFailureClassifier: null,
                failureDispositionClassifier: null)
        {
        }

        public StrictMessageHandler(
            IEventContextHandler eventContextHandler,
            IResponseService responseService,
            ILogger logger,
            IRetryPolicyProvider? retryPolicyProvider,
            MessagePipeline pipeline,
            MessageLifecycleNotifier lifecycleNotifier,
            IPermanentFailureClassifier permanentFailureClassifier = null)
            : this(
                eventContextHandler,
                responseService,
                logger,
                retryPolicyProvider,
                pipeline,
                lifecycleNotifier,
                permanentFailureClassifier,
                failureDispositionClassifier: null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="StrictMessageHandler"/> class.
        /// </summary>
        /// <param name="eventContextHandler">The event context handler.</param>
        /// <param name="responseService">The response service.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="retryPolicyProvider">The retry policy provider.</param>
        /// <param name="pipeline">The message pipeline.</param>
        /// <param name="lifecycleNotifier">The message lifecycle notifier.</param>
        /// <param name="permanentFailureClassifier">The legacy permanent-failure classifier.</param>
        /// <param name="failureDispositionClassifier">The failure disposition classifier.</param>
        public StrictMessageHandler(
            IEventContextHandler eventContextHandler,
            IResponseService responseService,
            ILogger? logger,
            IRetryPolicyProvider? retryPolicyProvider,
            MessagePipeline? pipeline,
            MessageLifecycleNotifier? lifecycleNotifier,
            IPermanentFailureClassifier? permanentFailureClassifier,
            IFailureDispositionClassifier? failureDispositionClassifier)
            : this(
                eventContextHandler,
                responseService,
                logger,
                retryPolicyProvider,
                pipeline,
                lifecycleNotifier,
                permanentFailureClassifier,
                failureDispositionClassifier,
                inboxDuplicateDetector: null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="StrictMessageHandler"/> class.
        /// </summary>
        /// <param name="eventContextHandler">The event context handler.</param>
        /// <param name="responseService">The response service.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="retryPolicyProvider">The retry policy provider.</param>
        /// <param name="pipeline">The message pipeline.</param>
        /// <param name="lifecycleNotifier">The message lifecycle notifier.</param>
        /// <param name="permanentFailureClassifier">The legacy permanent-failure classifier.</param>
        /// <param name="failureDispositionClassifier">The failure disposition classifier.</param>
        /// <param name="inboxDuplicateDetector">
        /// The optional inbox duplicate detector, consulted before the session-state guards so a
        /// redelivered duplicate is surfaced as a duplicate even when session state moved on.
        /// </param>
        public StrictMessageHandler(
            IEventContextHandler eventContextHandler,
            IResponseService responseService,
            ILogger? logger,
            IRetryPolicyProvider? retryPolicyProvider,
            MessagePipeline? pipeline,
            MessageLifecycleNotifier? lifecycleNotifier,
            IPermanentFailureClassifier? permanentFailureClassifier,
            IFailureDispositionClassifier? failureDispositionClassifier,
            InboxDuplicateDetector? inboxDuplicateDetector) : base(logger ?? NullLogger.Instance, pipeline!, lifecycleNotifier!, responseService)
        {
            _eventContextHandler = eventContextHandler;
            _responseService = responseService;
            _retryPolicyProvider = retryPolicyProvider;
            _permanentFailureClassifier = permanentFailureClassifier;
            _failureDispositionClassifier = failureDispositionClassifier
                ?? new DefaultFailureDispositionClassifier(_permanentFailureClassifier);
            _inboxDuplicateDetector = inboxDuplicateDetector;
            _logger = logger ?? NullLogger.Instance;
        }
#pragma warning restore CS0618

        public override async Task HandleEventRequest(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            try
            {
                LogInfo(messageContext, "Handle");

                // Inbox pre-check ahead of the session guard: a recorded EventRequest that
                // redelivers while its session is blocked by another event must surface as a
                // duplicate skip, not defer behind the blocker.
                if (await IsInboxDuplicate(messageContext, cancellationToken))
                {
                    await SendDuplicateResponseAndComplete(messageContext, "DuplicateDetected", cancellationToken);
                    return;
                }

                await VerifySessionIsNotBlocked(messageContext, cancellationToken);
                var discardedFailure = await HandleEventContent(messageContext, cancellationToken);
                if (discardedFailure is not null)
                {
                    await DiscardMessage(messageContext, discardedFailure, cancellationToken);
                    return;
                }

                if (messageContext.HandlerOutcome == HandlerOutcome.DuplicateDetected)
                {
                    await SendDuplicateResponseAndComplete(messageContext, "DuplicateDetected", cancellationToken);
                    return;
                }

                // PendingHandoff branch — handler handed off to an external system.
                // Send PendingHandoffResponse, block the session so siblings defer
                // until the Manager settles via CompleteHandoff / FailHandoff, and
                // skip the usual ResolutionResponse. If HandleEventContent threw,
                // execution never reaches here — the catch branches below own it.
                if (messageContext.HandlerOutcome == HandlerOutcome.PendingHandoff)
                {
                    await _responseService.SendPendingHandoffResponse(messageContext, messageContext.HandoffMetadata, cancellationToken);
                    await BlockSession(messageContext, cancellationToken);
                    await CompleteMessage(messageContext, cancellationToken);
                    LogInfo(messageContext, "Successfully processed (PendingHandoff)");
                    return;
                }

                await SendResolutionResponse(messageContext, cancellationToken);
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

                // Inbox pre-check ahead of the blocked-by-this guard: a successfully handled
                // RetryRequest leaves its session unblocked, so its redelivery would otherwise
                // fail the guard and complete with a normal response, hiding the duplicate. When
                // the crash happened between recording and unblocking, the session is still
                // blocked by this event — release it and drain deferred siblings before
                // completing the duplicate, or the session would stay blocked forever. When the
                // crash happened between unblocking and the deferred drain, the session is not
                // blocked at all — the duplicate path is then the only remaining drain trigger,
                // so run it here. A session blocked by an unrelated later event is left alone;
                // that blocker owns the drain.
                if (await IsInboxDuplicate(messageContext, cancellationToken))
                {
                    if (await messageContext.IsSessionBlockedByThis(cancellationToken))
                    {
                        await UnblockSession(messageContext, cancellationToken);
                        await ContinueWithAnyDeferredMessages(messageContext, cancellationToken);
                    }
                    else if (string.IsNullOrEmpty(await messageContext.GetBlockedByEventId(cancellationToken)))
                    {
                        await ContinueWithAnyDeferredMessages(messageContext, cancellationToken);
                    }

                    await SendDuplicateResponseAndComplete(messageContext, "RetryRequest DuplicateDetected", cancellationToken);
                    return;
                }

                await VerifySessionIsBlockedByThis(messageContext, cancellationToken);
                var discardedFailure = await HandleEventContent(messageContext, cancellationToken);
                await UnblockSession(messageContext, cancellationToken);
                await ContinueWithAnyDeferredMessages(messageContext, cancellationToken);
                if (discardedFailure is not null)
                {
                    await DiscardMessage(messageContext, discardedFailure, cancellationToken);
                    return;
                }
                if (messageContext.HandlerOutcome == HandlerOutcome.DuplicateDetected)
                {
                    await SendDuplicateResponseAndComplete(messageContext, "RetryRequest DuplicateDetected", cancellationToken);
                    return;
                }
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

                // Inbox pre-check: the decorator at the handler seam is record-only in hosted
                // compositions (one check, one record per delivery), so every entry point that
                // dispatches the handler must run the check itself. The session handling below
                // mirrors the normal resubmission path.
                if (await IsInboxDuplicate(messageContext, cancellationToken))
                {
                    if (await messageContext.IsSessionBlockedByThis(cancellationToken))
                        await UnblockSession(messageContext, cancellationToken);
                    await ContinueWithAnyDeferredMessages(messageContext, cancellationToken);
                    await SendDuplicateResponseAndComplete(messageContext, "Resubmission DuplicateDetected", cancellationToken);
                    return;
                }

                var discardedFailure = await HandleEventContent(messageContext, cancellationToken);
                if (await messageContext.IsSessionBlockedByThis(cancellationToken))
                    await UnblockSession(messageContext, cancellationToken);
                await ContinueWithAnyDeferredMessages(messageContext, cancellationToken);
                if (discardedFailure is not null)
                {
                    await DiscardMessage(messageContext, discardedFailure, cancellationToken);
                    return;
                }
                if (messageContext.HandlerOutcome == HandlerOutcome.DuplicateDetected)
                {
                    await SendDuplicateResponseAndComplete(messageContext, "Resubmission DuplicateDetected", cancellationToken);
                    return;
                }
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

        public override async Task HandleHandoffCompletedRequest(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            try
            {
                LogInfo(messageContext, "Handle (HandoffCompleted)");
                AuthorizeManagerRequest(messageContext);
                await VerifySessionIsBlockedByThis(messageContext, cancellationToken);
                await UnblockSession(messageContext, cancellationToken);
                await ContinueWithAnyDeferredMessages(messageContext, cancellationToken);
                // Existing ResolutionResponse path flips Pending → Completed on the
                // original audit row. The user handler is intentionally NOT invoked.
                await SendResolutionResponse(messageContext, cancellationToken);
                await CompleteMessage(messageContext, cancellationToken);
                LogInfo(messageContext, "Successfully processed (HandoffCompleted)");
            }
            catch (SessionBlockedException exception)
            {
                // The settlement's SessionId isn't blocked, or its EventId ≠
                // BlockedByEventId — a misaddressed or duplicate settlement. There is
                // no matching blocked event to unblock; the original is already
                // resolved. Mirror HandleRetryRequest's catch: surface it as resolved
                // in the Flow and Complete, rather than letting the base handler
                // swallow it and silently dead-letter. Do NOT unblock.
                LogError(messageContext, "HandoffCompleted settlement does not match a blocked session — surfacing as resolved", exception);
                await SendResolutionResponse(messageContext, cancellationToken);
                await CompleteMessage(messageContext, cancellationToken);
            }
        }

        public override async Task HandleHandoffFailedRequest(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            try
            {
                LogInfo(messageContext, "Handle (HandoffFailed)");
                AuthorizeManagerRequest(messageContext);
                await VerifySessionIsBlockedByThis(messageContext, cancellationToken);
                // Synthesise an exception from the inbound ErrorContent so the existing
                // SendErrorResponse path flips the audit row Pending → Failed with the
                // operator-supplied error text preserved verbatim. Session stays
                // blocked — the operator decides Resubmit / Skip from the WebApp.
                var handoffError = BuildHandoffError(messageContext);
                await SendErrorResponse(messageContext, handoffError, cancellationToken);
                await CompleteMessage(messageContext, cancellationToken);
                LogInfo(messageContext, "Successfully processed (HandoffFailed)");
            }
            catch (SessionBlockedException exception)
            {
                // The settlement's SessionId isn't blocked, or its EventId ≠
                // BlockedByEventId — a misaddressed or duplicate settlement. There is
                // no matching blocked event to flip to Failed, and a SendErrorResponse
                // here could mis-target a different event. Log an Error with full
                // metadata and Complete so the failure is surfaced where an operator
                // looks instead of silently dead-lettering via the base handler.
                LogError(messageContext, "HandoffFailed settlement does not match a blocked session — no matching event to fail", exception);
                await CompleteMessage(messageContext, cancellationToken);
            }
        }

        public override async Task HandleContinuationRequest(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            try
            {
                LogInfo(messageContext, "Handle (Continuation)");

                AuthorizeContinuationRequest(messageContext);
                IMessageContext deferredMessageContext = await ReceiveNextDeferredAndVerifyEventId(messageContext, true, cancellationToken);
                try
                {
                    await HandleEventRequest(deferredMessageContext, cancellationToken);
                }
                catch (EventContextHandlerException)
                {
                    // The nested dispatch settled the deferred message itself (error
                    // response sent, session blocked, message completed) — its sequence
                    // must stay popped.
                    throw;
                }
                catch (SessionBlockedException)
                {
                    // The nested dispatch re-deferred the message to the Deferred
                    // subscription and completed it — its reference lives there now.
                    throw;
                }
                catch (Exception)
                {
                    // Anything else (inbox check/record outage, transient handler
                    // failure, cancellation, unexpected) left the deferred message
                    // broker-deferred and unsettled after its only sequence reference
                    // was popped. Restore the reference so the redelivered
                    // continuation — or a later drain — can still reach it.
                    await RestoreDeferredBestEffort(messageContext, deferredMessageContext);
                    throw;
                }

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

        private async Task<bool> IsInboxDuplicate(IMessageContext messageContext, CancellationToken cancellationToken)
        {
            if (_inboxDuplicateDetector is null)
                return false;

            return await _inboxDuplicateDetector.IsDuplicateAsync(messageContext, cancellationToken);
        }

        private async Task SendDuplicateResponseAndComplete(
            IMessageContext messageContext,
            string logSuffix,
            CancellationToken cancellationToken)
        {
            await _responseService.SendDuplicateResponse(messageContext, cancellationToken);
            await CompleteMessage(messageContext, cancellationToken);
            LogInfo(messageContext, $"Successfully processed ({logSuffix})");
        }

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

        private Task SendRetryResponse(IMessageContext messageContext, TimeSpan messageDelay, CancellationToken cancellationToken = default) =>
            _responseService.SendRetryResponse(messageContext, messageDelay, cancellationToken);

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
            {
                // A popped mismatch was never dispatched; put its sequence back so a
                // later drain can still reach it instead of orphaning it.
                if (removeFromQueue && nextDeferred != null)
                    await RestoreDeferredBestEffort(messageContext, nextDeferred);
                throw new NextDeferredException($"Unable to continue with {messageContext.EventId}, because it is not the next deferred event request in this session.");
            }

            return nextDeferred;
        }

        private async Task RestoreDeferredBestEffort(
            IMessageContext messageContext,
            IMessageContext deferredMessageContext)
        {
            try
            {
                // Cancellation is itself one of the failure modes that leaves the popped
                // message unsettled, so the caller's token is typically already cancelled
                // here — reusing it would cancel the session-state write and silently skip
                // the restore. The recovery I/O runs under its own bounded token instead;
                // the original failure still owns settlement and rethrows unchanged.
                using var restoreCancellation = new CancellationTokenSource(DeferredRestoreTimeout);
                await messageContext.RestoreNextDeferred(deferredMessageContext, restoreCancellation.Token);
            }
            catch (Exception restoreException)
            {
                // Best-effort: the original failure still owns settlement of the outer
                // message; losing the restore only degrades to today's behaviour, so it
                // must never mask that failure.
                LogError(messageContext, "Failed to restore the popped deferred sequence; the deferred message may need operator recovery", restoreException);
            }
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
                throw new SessionBlockedException($"Session {messageContext.SessionId} is blocked by {blockedBy}", blockedBy);
            }
        }

        private async Task VerifySessionIsNotBlocked(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            var blockedBy = await messageContext.GetBlockedByEventId(cancellationToken);
            if (!string.IsNullOrEmpty(blockedBy))
                throw new SessionBlockedException($"Session {messageContext.SessionId} is blocked by {blockedBy}", blockedBy);
        }

        private async Task<DiscardedFailure?> HandleEventContent(IMessageContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                await _eventContextHandler.Handle(context, cancellationToken);
                return null;
            }
            catch (TransientException)
            {
                throw;
            }
            catch (EventHandlerNotFoundException)
            {
                throw;
            }
            catch (PermanentFailureException)
            {
                // A permanent failure (e.g. an invalid or unknown-type CloudEvent
                // rejected by the CloudEvents validator) must dead-letter, not go
                // through the retry/error-response path. Let it propagate to
                // MessageHandler.Handle's dedicated dead-letter catch.
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Cooperative shutdown must remain cancellation all the way through
                // MessageHandler; wrapping it would trigger error responses, session
                // blocking, retries, and settlement.
                throw;
            }
            catch (Exception exception)
            {
                var disposition = _failureDispositionClassifier.Classify(
                    exception,
                    context.EventTypeId,
                    context.To);

                switch (disposition)
                {
                    case FailureDisposition.Retry:
                        throw new EventContextHandlerException(exception)
                        {
                            Source = exception.Source
                        };

                    case FailureDisposition.DeadLetter:
                        throw new PermanentFailureException(exception);

                    case FailureDisposition.Discard:
                        var classifierName = _failureDispositionClassifier.GetType().FullName
                            ?? _failureDispositionClassifier.GetType().Name;
                        return new DiscardedFailure(exception, classifierName);

                    default:
                        throw new ArgumentOutOfRangeException(
                            nameof(disposition),
                            disposition,
                            "Failure disposition classifier returned an unsupported value.");
                }
            }
        }

        private async Task DiscardMessage(
            IMessageContext messageContext,
            DiscardedFailure discardedFailure,
            CancellationToken cancellationToken)
        {
            _logger.LogWarning(
                discardedFailure.Exception,
                "Discarding failed message without retry or dead-letter. Classifier:{ClassifierName}, EventTypeId:{EventTypeId}, EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
                discardedFailure.ClassifierName,
                messageContext.EventTypeId,
                messageContext.GetEventIdOrDefault(),
                messageContext.GetMessageIdOrDefault(),
                messageContext.GetSessionIdOrDefault());
            await _responseService.SendDiscardResponse(
                messageContext,
                discardedFailure.Exception,
                discardedFailure.ClassifierName,
                cancellationToken);
            await CompleteMessage(messageContext, cancellationToken);
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
            var eventTypeId = messageContext.EventTypeId;
            var exceptionText = $"{exception?.InnerException} {exception}";
            var retryCount = messageContext.RetryCount ?? 0;

            if (_retryPolicyProvider != null)
            {
                var policy = _retryPolicyProvider.GetRetryPolicy(eventTypeId, exceptionText, messageContext.To);
                if (policy != null && retryCount < policy.MaxRetries)
                {
                    var delay = policy.GetDelay(retryCount);
                    await SendRetryResponse(messageContext, delay, cancellationToken);
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

        // HandoffFailedRequest carries errorText/errorType in
        // MessageContent.ErrorContent. Wrap them in a synthetic
        // EventContextHandlerException so the existing SendErrorResponse path
        // produces a response whose ErrorText preserves the operator-supplied
        // text verbatim. ErrorType is not round-tripped through this exception
        // shape — the response's ErrorType reflects the synthetic wrapper, not
        // the operator-supplied errorType.
        private static EventContextHandlerException BuildHandoffError(IMessageContext messageContext)
        {
            var errorContent = messageContext?.MessageContent?.ErrorContent;
            var errorText = errorContent?.ErrorText ?? "Handoff failed.";
            var inner = new HandoffFailedException(errorText, errorContent?.ErrorType);
            return new EventContextHandlerException(inner);
        }

        // Identity fields are read through the non-throwing accessors: the Service Bus context
        // throws for fields absent on the wire, and diagnostic logging must never abort
        // processing of such messages (e.g. the missing-MessageId inbox bypass path).
        private void LogInfo(IMessageContext messageContext, string prefixMessage)
        {
            _logger.LogInformation("{Prefix} EventTypeId:{EventTypeId}, EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
                prefixMessage, messageContext.EventTypeId, messageContext.GetEventIdOrDefault(), messageContext.GetMessageIdOrDefault(), messageContext.GetSessionIdOrDefault());
        }

        private void LogError(IMessageContext messageContext, string prefixMessage, Exception exception)
        {
            _logger.LogError(exception, "{Prefix} EventTypeId:{EventTypeId}, EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
                prefixMessage, messageContext.EventTypeId, messageContext.GetEventIdOrDefault(), messageContext.GetMessageIdOrDefault(), messageContext.GetSessionIdOrDefault());
        }

        private sealed record DiscardedFailure(Exception Exception, string ClassifierName);
    }
}
