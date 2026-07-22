#pragma warning disable CA1707, CA1515, CA2007
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.Testing;

namespace NimBus.Core.Tests;

[TestClass]
public class StrictMessageHandlerTests
{
    // ── HandleEventRequest ──────────────────────────────────────────────

    [TestMethod]
    public async Task HandleEventRequest_NormalEvent_HandlesAndCompletes()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest, eventTypeId: "OrderPlaced");
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, handler.HandleCalls);
        Assert.AreEqual(1, response.ResolutionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_Duplicate_SendsDuplicateResponseAndCompletes()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest, eventTypeId: "OrderPlaced");
        var handler = new FakeEventContextHandler
        {
            OnHandle = context => context.HandlerOutcome = HandlerOutcome.DuplicateDetected,
        };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, handler.HandleCalls);
        Assert.AreEqual(1, response.DuplicateCalls);
        Assert.AreEqual(0, response.ResolutionCalls);
        Assert.AreEqual(0, response.PendingHandoffCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_EventHandlerNotFound_SendsUnsupportedAndCompletes()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        var handler = new FakeEventContextHandler { ThrowOnHandle = new EventHandlerNotFoundException("not found") };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.UnsupportedCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
        Assert.AreEqual(0, response.ResolutionCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_SessionBlocked_SendsDeferralAndDefersToSubscription()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        ctx.BlockedByEventId = "other-event";
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        // SessionBlockedException is caught by base MessageHandler (swallowed)
        await sut.Handle(ctx);

        Assert.AreEqual(1, response.DeferralCalls);
        Assert.AreEqual(1, response.SendToDeferredSubscriptionCalls);
        Assert.AreEqual(1, ctx.IncrementDeferredCountCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_HandlerThrows_SendsErrorBlocksSessionAndCompletes()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        var handler = new FakeEventContextHandler { ThrowOnHandle = new InvalidOperationException("boom") };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        // EventContextHandlerException is caught by base MessageHandler (swallowed)
        await sut.Handle(ctx);

        Assert.AreEqual(1, response.ErrorCalls);
        Assert.AreEqual(1, ctx.BlockSessionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_TransientException_AbandonsMessage()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        var handler = new FakeEventContextHandler { ThrowOnHandle = new TransientException("transient") };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.AbandonCalls);
        Assert.AreEqual(0, ctx.CompletedCalls);
        Assert.AreEqual(0, ctx.DeadLetterCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_CallerCancellation_PropagatesWithoutFailureSideEffects()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        var handler = new FakeEventContextHandler
        {
            ThrowOnHandle = new OperationCanceledException(cancellation.Token),
        };
        var response = new FakeResponseService();
        var retryProvider = new FakeRetryPolicyProvider
        {
            PolicyToReturn = new RetryPolicy { MaxRetries = 3 },
        };
        var sut = new StrictMessageHandler(handler, response, NullLogger.Instance, retryProvider);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => sut.Handle(ctx, cancellation.Token));

        Assert.AreEqual(0, response.ErrorCalls);
        Assert.AreEqual(0, response.RetryCalls);
        Assert.AreEqual(0, response.ResolutionCalls);
        Assert.AreEqual(0, response.DeadLetterCalls);
        Assert.AreEqual(0, ctx.BlockSessionCalls);
        Assert.AreEqual(0, ctx.CompletedCalls);
        Assert.AreEqual(0, ctx.DeadLetterCalls);
        Assert.AreEqual(0, ctx.AbandonCalls);
        Assert.AreEqual(0, retryProvider.GetRetryPolicyCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_PermanentPayloadFailure_DeadLettersWithoutRetry()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        var handler = new FakeEventContextHandler
        {
            ThrowOnHandle = new PermanentFailureException(new FormatException("Malformed payload.")),
        };
        var response = new FakeResponseService();
        var retryProvider = new FakeRetryPolicyProvider
        {
            PolicyToReturn = new RetryPolicy { MaxRetries = 3 },
        };
        var sut = new StrictMessageHandler(handler, response, NullLogger.Instance, retryProvider);

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.DeadLetterCalls);
        Assert.AreEqual(1, response.DeadLetterCalls);
        Assert.AreEqual(0, response.ErrorCalls);
        Assert.AreEqual(0, response.RetryCalls);
        Assert.AreEqual(0, ctx.BlockSessionCalls);
        Assert.AreEqual(0, ctx.CompletedCalls);
        Assert.AreEqual(0, retryProvider.GetRetryPolicyCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_Discard_CompletesWithoutDeadLetterOrRetryAndNotifiesLifecycle()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        var failure = new InvalidOperationException("Known bad event version.");
        var handler = new FakeEventContextHandler { ThrowOnHandle = failure };
        var response = new FakeResponseService();
        var retryProvider = new FakeRetryPolicyProvider
        {
            PolicyToReturn = new RetryPolicy { MaxRetries = 3 },
        };
        var classifier = new FakeFailureDispositionClassifier(FailureDisposition.Discard);
        var observer = new RecordingLifecycleObserver();
        var notifier = new MessageLifecycleNotifier([observer]);
#pragma warning disable CS0618
        var sut = new StrictMessageHandler(
            handler,
            response,
            NullLogger.Instance,
            retryProvider,
            pipeline: null,
            lifecycleNotifier: notifier,
            permanentFailureClassifier: new AlwaysPermanentFailureClassifier(),
            failureDispositionClassifier: classifier);
#pragma warning restore CS0618

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.CompletedCalls);
        Assert.AreEqual(0, ctx.DeadLetterCalls);
        Assert.AreEqual(0, ctx.BlockSessionCalls);
        Assert.AreEqual(1, response.DiscardCalls);
        Assert.AreSame(failure, response.LastDiscardException);
        Assert.AreEqual(typeof(FakeFailureDispositionClassifier).FullName, response.LastDiscardClassifierName);
        Assert.AreEqual(0, response.ErrorCalls);
        Assert.AreEqual(0, response.RetryCalls);
        Assert.AreEqual(0, response.ResolutionCalls);
        Assert.AreEqual(0, retryProvider.GetRetryPolicyCalls);
        Assert.AreSame(failure, classifier.LastException);
        Assert.AreEqual(ctx.EventTypeId, classifier.LastEventTypeId);
        Assert.AreEqual(ctx.To, classifier.LastEndpointName);
        Assert.AreEqual(1, observer.ReceivedCalls);
        Assert.AreEqual(1, observer.CompletedCalls);
        Assert.AreEqual(0, observer.FailedCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_Discard_DoesNotBlockNextMessageInSession()
    {
        var first = CreateContext(messageType: MessageType.EventRequest, eventId: "event-1");
        var second = CreateContext(messageType: MessageType.EventRequest, eventId: "event-2");
        var attempts = 0;
        var handler = new FakeEventContextHandler
        {
            OnHandle = _ =>
            {
                attempts++;
                if (attempts == 1)
                    throw new InvalidOperationException("discard first");
            },
        };
        var response = new FakeResponseService();
        var classifier = new FakeFailureDispositionClassifier(FailureDisposition.Discard);
        var sut = new StrictMessageHandler(
            handler,
            response,
            NullLogger.Instance,
            retryPolicyProvider: null,
            pipeline: null,
            lifecycleNotifier: null,
            permanentFailureClassifier: null,
            failureDispositionClassifier: classifier);

        await sut.Handle(first);
        await sut.Handle(second);

        Assert.AreEqual(first.SessionId, second.SessionId);
        Assert.AreEqual(2, handler.HandleCalls);
        Assert.AreEqual(1, first.CompletedCalls);
        Assert.AreEqual(0, first.BlockSessionCalls);
        Assert.AreEqual(1, second.CompletedCalls);
        Assert.AreEqual(1, response.DiscardCalls);
        Assert.AreEqual(1, response.ResolutionCalls);
    }

    [TestMethod]
    public async Task HandleRetryRequest_Discard_UnblocksSessionAndContinuesDeferredMessages()
    {
        var ctx = CreateContext(messageType: MessageType.RetryRequest);
        ctx.IsSessionBlockedByThisResult = true;
        ctx.DeferredCountResult = 1;
        var handler = new FakeEventContextHandler
        {
            ThrowOnHandle = new InvalidOperationException("discard retry"),
        };
        var response = new FakeResponseService();
        var sut = new StrictMessageHandler(
            handler,
            response,
            NullLogger.Instance,
            retryPolicyProvider: null,
            pipeline: null,
            lifecycleNotifier: null,
            permanentFailureClassifier: null,
            failureDispositionClassifier: new FakeFailureDispositionClassifier(FailureDisposition.Discard));

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.UnblockSessionCalls);
        Assert.AreEqual(1, response.ProcessDeferredCalls);
        Assert.AreEqual(1, response.DiscardCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
        Assert.AreEqual(0, response.ResolutionCalls);
        Assert.AreEqual(0, ctx.DeadLetterCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_LegacyPermanentClassifier_BridgesToDeadLetter()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        var handler = new FakeEventContextHandler
        {
            ThrowOnHandle = new InvalidOperationException("legacy permanent"),
        };
        var response = new FakeResponseService();
        var retryProvider = new FakeRetryPolicyProvider
        {
            PolicyToReturn = new RetryPolicy { MaxRetries = 3 },
        };
#pragma warning disable CS0618
        var sut = new StrictMessageHandler(
            handler,
            response,
            NullLogger.Instance,
            retryProvider,
            pipeline: null,
            lifecycleNotifier: null,
            permanentFailureClassifier: new AlwaysPermanentFailureClassifier());
#pragma warning restore CS0618

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.DeadLetterCalls);
        Assert.AreEqual(1, response.DeadLetterCalls);
        Assert.AreEqual(0, response.ErrorCalls);
        Assert.AreEqual(0, response.RetryCalls);
        Assert.AreEqual(0, retryProvider.GetRetryPolicyCalls);
    }

    // ── PendingHandoff outcome ──────────────────────────────────────────

    [TestMethod]
    public async Task HandleEventRequest_HandlerSignalsPendingHandoff_SendsHandoffResponseAndBlocksSession()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        var metadata = new HandoffMetadata("DMF import in flight", "JOB-42", TimeSpan.FromMinutes(5));
        var handler = new FakeEventContextHandler
        {
            OnHandle = c =>
            {
                c.HandlerOutcome = HandlerOutcome.PendingHandoff;
                c.HandoffMetadata = metadata;
            },
        };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, handler.HandleCalls);
        Assert.AreEqual(1, response.PendingHandoffCalls, "PendingHandoffResponse should fire");
        Assert.AreEqual(0, response.ResolutionCalls, "ResolutionResponse must NOT fire when handler signals PendingHandoff");
        Assert.AreSame(metadata, response.LastPendingHandoffMetadata);
        Assert.AreEqual(1, ctx.BlockSessionCalls, "Session must be blocked so siblings defer");
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_HandlerSignalsPendingHandoffThenThrows_FailurePathWins()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        var handler = new FakeEventContextHandler
        {
            OnHandle = c =>
            {
                c.HandlerOutcome = HandlerOutcome.PendingHandoff;
                c.HandoffMetadata = new HandoffMetadata("about to fail", null, null);
            },
            ThrowOnHandle = new InvalidOperationException("boom"),
        };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(0, response.PendingHandoffCalls, "PendingHandoff must not fire when handler throws");
        Assert.AreEqual(1, response.ErrorCalls, "Failure path wins");
        Assert.AreEqual(1, ctx.BlockSessionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_HandlerCallsMarkPendingHandoffTwice_LastCallWins()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        var first = new HandoffMetadata("first", "JOB-1", TimeSpan.FromMinutes(1));
        var second = new HandoffMetadata("second", "JOB-2", TimeSpan.FromMinutes(10));
        var handler = new FakeEventContextHandler
        {
            OnHandle = c =>
            {
                c.HandlerOutcome = HandlerOutcome.PendingHandoff;
                c.HandoffMetadata = first;
                // Simulate a second MarkPendingHandoff call — last write wins.
                c.HandoffMetadata = second;
            },
        };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.PendingHandoffCalls);
        Assert.AreSame(second, response.LastPendingHandoffMetadata, "Second MarkPendingHandoff call must overwrite the first");
        Assert.AreSame(second, ctx.HandoffMetadata);
    }

    // ── HandleHandoffCompletedRequest ───────────────────────────────────

    [TestMethod]
    public async Task HandleHandoffCompletedRequest_AuthorizedAndBlockedByThis_UnblocksAndSendsResolution()
    {
        var ctx = CreateContext(messageType: MessageType.HandoffCompletedRequest, from: "Manager");
        ctx.IsSessionBlockedByThisResult = true;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.UnblockSessionCalls);
        Assert.AreEqual(1, response.ResolutionCalls, "Resolver-bound ResolutionResponse flips Pending → Completed");
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleHandoffCompletedRequest_FromNonManager_DeadLetters()
    {
        var ctx = CreateContext(messageType: MessageType.HandoffCompletedRequest, from: "SomeEndpoint");
        ctx.IsSessionBlockedByThisResult = true;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        // UnauthorizedAccessException flows through the base MessageHandler -> dead-letters
        Assert.AreEqual(1, ctx.DeadLetterCalls);
        Assert.AreEqual(0, ctx.UnblockSessionCalls);
        Assert.AreEqual(0, response.ResolutionCalls);
    }

    [TestMethod]
    public async Task HandleHandoffCompletedRequest_MismatchedEventId_SendsResolutionAndCompletes()
    {
        // Settlement's EventId ≠ BlockedByEventId — a misaddressed or duplicate
        // settlement. VerifySessionIsBlockedByThis throws SessionBlockedException.
        // Rather than silently dead-lettering, surface it as resolved in the Flow.
        var ctx = CreateContext(messageType: MessageType.HandoffCompletedRequest, from: "Manager");
        ctx.IsSessionBlockedByThisResult = false;
        ctx.BlockedByEventId = "different-event";
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.ResolutionCalls, "Unmatched settlement is surfaced as resolved, not silently dropped");
        Assert.AreEqual(1, ctx.CompletedCalls, "Message must be completed, not left for redelivery");
        Assert.AreEqual(0, ctx.UnblockSessionCalls, "Must NOT unblock — this settlement does not own the block");
        Assert.AreEqual(0, ctx.DeadLetterCalls, "Must not silently dead-letter");
    }

    [TestMethod]
    public async Task HandleHandoffCompletedRequest_SessionNotBlockedAtAll_SendsResolutionAndCompletes()
    {
        // Session isn't blocked at all (already resolved / wrong session).
        var ctx = CreateContext(messageType: MessageType.HandoffCompletedRequest, from: "Manager");
        ctx.IsSessionBlockedByThisResult = false;
        ctx.BlockedByEventId = null;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.ResolutionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
        Assert.AreEqual(0, ctx.UnblockSessionCalls);
        Assert.AreEqual(0, ctx.DeadLetterCalls);
    }

    [TestMethod]
    public async Task HandleHandoffFailedRequest_AuthorizedAndBlockedByThis_SendsErrorAndKeepsSessionBlocked()
    {
        var ctx = CreateContext(messageType: MessageType.HandoffFailedRequest, from: "Manager");
        ctx.IsSessionBlockedByThisResult = true;
        ctx.MessageContent = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" },
            ErrorContent = new ErrorContent
            {
                ErrorText = "DMF rejected: invalid postal code",
                ErrorType = "DmfValidationError",
            },
        };
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.ErrorCalls, "Resolver-bound ErrorResponse flips Pending → Failed");
        Assert.AreEqual(0, ctx.UnblockSessionCalls, "Session stays blocked — operator decides Resubmit/Skip");
        Assert.AreEqual(1, ctx.CompletedCalls);
        Assert.IsNotNull(response.LastErrorException);
        var errorMessage = response.LastErrorException.InnerException?.Message ?? response.LastErrorException.Message;
        StringAssert.Contains(errorMessage, "DMF rejected: invalid postal code", "Operator-supplied errorText must be preserved verbatim");
    }

    [TestMethod]
    public async Task HandleHandoffFailedRequest_MismatchedEventId_CompletesWithoutErrorResponse()
    {
        // Settlement's EventId ≠ BlockedByEventId. There is no matching blocked
        // event to flip to Failed, and a SendErrorResponse here could mis-target a
        // different event — so log an Error and Complete so it doesn't silently
        // dead-letter. No error response is sent (it would mis-target).
        var ctx = CreateContext(messageType: MessageType.HandoffFailedRequest, from: "Manager");
        ctx.IsSessionBlockedByThisResult = false;
        ctx.BlockedByEventId = "different-event";
        ctx.MessageContent = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" },
            ErrorContent = new ErrorContent { ErrorText = "DMF rejected", ErrorType = "DmfValidationError" },
        };
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.CompletedCalls, "Message must be completed, not left to silently dead-letter");
        Assert.AreEqual(0, response.ErrorCalls, "Must NOT send an ErrorResponse — it could mis-target a different event");
        Assert.AreEqual(0, ctx.UnblockSessionCalls, "Must not touch the block — no matching blocked event");
        Assert.AreEqual(0, ctx.DeadLetterCalls, "Must not silently dead-letter");
    }

    [TestMethod]
    public async Task HandleHandoffFailedRequest_SessionNotBlockedAtAll_CompletesWithoutErrorResponse()
    {
        var ctx = CreateContext(messageType: MessageType.HandoffFailedRequest, from: "Manager");
        ctx.IsSessionBlockedByThisResult = false;
        ctx.BlockedByEventId = null;
        ctx.MessageContent = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" },
            ErrorContent = new ErrorContent { ErrorText = "DMF rejected", ErrorType = "DmfValidationError" },
        };
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.CompletedCalls);
        Assert.AreEqual(0, response.ErrorCalls);
        Assert.AreEqual(0, ctx.UnblockSessionCalls);
        Assert.AreEqual(0, ctx.DeadLetterCalls);
    }

    [TestMethod]
    public async Task HandleHandoffFailedRequest_FromNonManager_DeadLetters()
    {
        var ctx = CreateContext(messageType: MessageType.HandoffFailedRequest, from: "SomeEndpoint");
        ctx.IsSessionBlockedByThisResult = true;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        // UnauthorizedAccessException flows through the base MessageHandler -> dead-letters
        Assert.AreEqual(1, ctx.DeadLetterCalls);
        Assert.AreEqual(0, response.ErrorCalls);
        Assert.AreEqual(0, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleHandoffCompletedRequest_DoesNotInvokeUserHandler()
    {
        var ctx = CreateContext(messageType: MessageType.HandoffCompletedRequest, from: "Manager");
        ctx.IsSessionBlockedByThisResult = true;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(0, handler.HandleCalls, "HandoffCompleted must NOT re-invoke the user handler");
    }

    // ── Dead-letter Resolver notification ───────────────────────────────

    [TestMethod]
    public async Task HandleResubmissionRequest_FromNonManager_NotifiesResolverOfDeadLetter()
    {
        // UnauthorizedAccessException flows through the base MessageHandler's
        // unexpected-exception catch — should both DeadLetter and notify the Resolver.
        var ctx = CreateContext(messageType: MessageType.ResubmissionRequest, from: "SomeEndpoint");
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.DeadLetterCalls);
        Assert.AreEqual(1, response.DeadLetterCalls);
        Assert.AreEqual("Failed to handle message.", response.LastDeadLetterReason);
        Assert.IsInstanceOfType(response.LastDeadLetterException, typeof(UnauthorizedAccessException));
    }

    [TestMethod]
    public async Task HandleEventRequest_HandlerThrowsWithRetryDefinition_SendsRetryResponse()
    {
        // "AliceSaidHelloWithRetry" has RetryCount=1, RetryDelay=1
        var ctx = CreateContext(messageType: MessageType.EventRequest, eventTypeId: "AliceSaidHelloWithRetry");
        ctx.RetryCount = 0;
        var handler = new FakeEventContextHandler { ThrowOnHandle = new InvalidOperationException("boom") };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.RetryCalls);
        Assert.AreEqual(1, response.LastRetryDelayMinutes);
    }

    [TestMethod]
    public async Task HandleEventRequest_RetryPolicy_SchedulesWithSubMinutePrecision()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest, eventTypeId: "OrderPlaced");
        var handler = new FakeEventContextHandler { ThrowOnHandle = new InvalidOperationException("boom") };
        var sender = new InMemoryMessageBus();
        var retryProvider = new FakeRetryPolicyProvider
        {
            PolicyToReturn = new RetryPolicy
            {
                MaxRetries = 1,
                BaseDelay = TimeSpan.FromSeconds(90),
            },
        };
        var sut = new StrictMessageHandler(handler, new ResponseService(sender), NullLogger.Instance, retryProvider);
        var before = DateTimeOffset.UtcNow;

        await sut.Handle(ctx);

        var after = DateTimeOffset.UtcNow;
        var scheduled = sender.ScheduledMessages.Single();
        Assert.AreEqual(Constants.RetryId, scheduled.Message.To);
        Assert.IsTrue(scheduled.ScheduledTime >= before.AddSeconds(90));
        Assert.IsTrue(scheduled.ScheduledTime <= after.AddSeconds(90));
    }

    [TestMethod]
    public async Task HandleEventRequest_LegacyResponseService_RoundsPrecisePolicyDelay()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest, eventTypeId: "OrderPlaced");
        var handler = new FakeEventContextHandler { ThrowOnHandle = new InvalidOperationException("boom") };
        var response = new FakeResponseService();
        var retryProvider = new FakeRetryPolicyProvider
        {
            PolicyToReturn = new RetryPolicy
            {
                MaxRetries = 1,
                BaseDelay = TimeSpan.FromSeconds(90),
            },
        };
        var sut = new StrictMessageHandler(handler, response, NullLogger.Instance, retryProvider);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.RetryCalls);
        Assert.AreEqual(2, response.LastRetryDelayMinutes);
    }

    [TestMethod]
    public async Task HandleEventRequest_HandlerThrowsRetryCountExceeded_NoRetryResponse()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest, eventTypeId: "AliceSaidHelloWithRetry");
        ctx.RetryCount = 1; // equals RetryCount limit
        var handler = new FakeEventContextHandler { ThrowOnHandle = new InvalidOperationException("boom") };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(0, response.RetryCalls, "Should not retry when retry count is at the limit");
    }

    [TestMethod]
    public async Task HandleEventRequest_HandlerThrows_RetryLookupUsesUserPropertyEventTypeId()
    {
        // EventTypeId is backed by a message user property (no body deserialization
        // required). CheckForRetry must key the retry-policy lookup off that
        // authoritative value, NOT off the deserialized body's
        // MessageContent.EventContent.EventTypeId. Here the two intentionally differ.
        var ctx = CreateContext(messageType: MessageType.EventRequest, eventTypeId: "UserPropEventType");
        ctx.MessageContent.EventContent.EventTypeId = "BodyEventType"; // must NOT be used
        var handler = new FakeEventContextHandler { ThrowOnHandle = new InvalidOperationException("boom") };
        var response = new FakeResponseService();
        var retryProvider = new FakeRetryPolicyProvider();
        var sut = new StrictMessageHandler(handler, response, NullLogger.Instance, retryProvider);

        await sut.Handle(ctx);

        Assert.AreEqual(1, retryProvider.GetRetryPolicyCalls, "CheckForRetry must consult the retry policy provider");
        Assert.AreEqual("UserPropEventType", retryProvider.LastEventTypeId,
            "Retry lookup must use the user-property EventTypeId, not the deserialized body value");
    }

    // ── HandleRetryRequest ──────────────────────────────────────────────

    [TestMethod]
    public async Task HandleRetryRequest_BlockedByThis_HandlesUnblocksAndCompletes()
    {
        var ctx = CreateContext(messageType: MessageType.RetryRequest);
        ctx.IsSessionBlockedByThisResult = true;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, handler.HandleCalls);
        Assert.AreEqual(1, ctx.UnblockSessionCalls);
        Assert.AreEqual(1, response.ResolutionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleRetryRequest_Duplicate_UnblocksAndSendsDuplicateResponse()
    {
        var ctx = CreateContext(messageType: MessageType.RetryRequest);
        ctx.IsSessionBlockedByThisResult = true;
        var handler = new FakeEventContextHandler
        {
            OnHandle = context => context.HandlerOutcome = HandlerOutcome.DuplicateDetected,
        };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.UnblockSessionCalls);
        Assert.AreEqual(1, response.DuplicateCalls);
        Assert.AreEqual(0, response.ResolutionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleRetryRequest_BlockedByThis_WithLegacyDeferred_SendsContinuationRequest()
    {
        var deferredCtx = CreateContext(messageType: MessageType.EventRequest, eventId: "deferred-event");
        var ctx = CreateContext(messageType: MessageType.RetryRequest);
        ctx.IsSessionBlockedByThisResult = true;
        ctx.NextDeferredResult = deferredCtx;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.ContinuationCalls);
        Assert.AreEqual(0, response.ProcessDeferredCalls);
    }

    [TestMethod]
    public async Task HandleRetryRequest_BlockedByThis_WithNewDeferred_SendsProcessDeferredRequest()
    {
        var ctx = CreateContext(messageType: MessageType.RetryRequest);
        ctx.IsSessionBlockedByThisResult = true;
        ctx.NextDeferredResult = null; // no legacy deferred
        ctx.DeferredCountResult = 3;  // but new-style deferred exist
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(0, response.ContinuationCalls);
        Assert.AreEqual(1, response.ProcessDeferredCalls);
    }

    [TestMethod]
    public async Task HandleRetryRequest_NotBlockedByThis_SendsResolutionAndCompletes()
    {
        var ctx = CreateContext(messageType: MessageType.RetryRequest);
        ctx.IsSessionBlockedByThisResult = false;
        ctx.BlockedByEventId = "different-event";
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(0, handler.HandleCalls, "Should not handle when not blocked by this");
        Assert.AreEqual(1, response.ResolutionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleRetryRequest_HandlerThrows_SendsErrorAndCompletes()
    {
        var ctx = CreateContext(messageType: MessageType.RetryRequest);
        ctx.IsSessionBlockedByThisResult = true;
        var handler = new FakeEventContextHandler { ThrowOnHandle = new InvalidOperationException("boom") };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.ErrorCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
        Assert.AreEqual(0, ctx.UnblockSessionCalls, "Should not unblock on failure");
    }

    // ── HandleResubmissionRequest ───────────────────────────────────────

    [TestMethod]
    public async Task HandleResubmissionRequest_FromManager_HandlesAndCompletes()
    {
        var ctx = CreateContext(messageType: MessageType.ResubmissionRequest, from: "Manager");
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, handler.HandleCalls);
        Assert.AreEqual(1, response.ResolutionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleResubmissionRequest_Duplicate_SendsDuplicateResponse()
    {
        var ctx = CreateContext(messageType: MessageType.ResubmissionRequest, from: "Manager");
        var handler = new FakeEventContextHandler
        {
            OnHandle = context => context.HandlerOutcome = HandlerOutcome.DuplicateDetected,
        };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.DuplicateCalls);
        Assert.AreEqual(0, response.ResolutionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleResubmissionRequest_FromManager_BlockedByThis_Unblocks()
    {
        var ctx = CreateContext(messageType: MessageType.ResubmissionRequest, from: "Manager");
        ctx.IsSessionBlockedByThisResult = true;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.UnblockSessionCalls);
    }

    [TestMethod]
    public async Task HandleResubmissionRequest_FromManager_NotBlockedByThis_SkipsUnblock()
    {
        var ctx = CreateContext(messageType: MessageType.ResubmissionRequest, from: "Manager");
        ctx.IsSessionBlockedByThisResult = false;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(0, ctx.UnblockSessionCalls);
    }

    [TestMethod]
    public async Task HandleResubmissionRequest_FromNonManager_DeadLetters()
    {
        var ctx = CreateContext(messageType: MessageType.ResubmissionRequest, from: "SomeEndpoint");
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        // UnauthorizedAccessException is caught by base MessageHandler -> dead-letters
        Assert.AreEqual(1, ctx.DeadLetterCalls);
        Assert.AreEqual(0, handler.HandleCalls);
    }

    [TestMethod]
    public async Task HandleResubmissionRequest_EventHandlerNotFound_SendsUnsupportedAndCompletes()
    {
        var ctx = CreateContext(messageType: MessageType.ResubmissionRequest, from: "Manager");
        var handler = new FakeEventContextHandler { ThrowOnHandle = new EventHandlerNotFoundException("not found") };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.UnsupportedCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleResubmissionRequest_HandlerThrows_SendsErrorAndCompletes()
    {
        var ctx = CreateContext(messageType: MessageType.ResubmissionRequest, from: "Manager");
        var handler = new FakeEventContextHandler { ThrowOnHandle = new InvalidOperationException("boom") };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.ErrorCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    // ── HandleSkipRequest ───────────────────────────────────────────────

    [TestMethod]
    public async Task HandleSkipRequest_FromManager_BlockedByThis_UnblocksAndSendsSkip()
    {
        var ctx = CreateContext(messageType: MessageType.SkipRequest, from: "Manager");
        ctx.IsSessionBlockedByThisResult = true;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.UnblockSessionCalls);
        Assert.AreEqual(1, response.SkipCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleSkipRequest_NotBlockedByThis_SendsSkipAndCompletes()
    {
        var ctx = CreateContext(messageType: MessageType.SkipRequest, from: "Manager");
        ctx.IsSessionBlockedByThisResult = false;
        ctx.BlockedByEventId = "different-event";
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.SkipCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleSkipRequest_FromNonManager_DeadLetters()
    {
        var ctx = CreateContext(messageType: MessageType.SkipRequest, from: "SomeEndpoint");
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.DeadLetterCalls);
        Assert.AreEqual(0, response.SkipCalls);
    }

    // ── HandleContinuationRequest ───────────────────────────────────────

    [TestMethod]
    public async Task HandleContinuationRequest_FromContinuation_MatchingEventId_HandlesDeferredAndCompletes()
    {
        var deferredCtx = CreateContext(messageType: MessageType.EventRequest, eventId: "event-1");
        var ctx = CreateContext(messageType: MessageType.ContinuationRequest, from: "Continuation", eventId: "event-1");
        ctx.NextDeferredWithPopResult = deferredCtx;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, handler.HandleCalls, "Should handle the deferred event");
        Assert.AreEqual(1, ctx.CompletedCalls, "Should complete the continuation message");
        Assert.AreEqual(0, ctx.RestoreNextDeferredCalls, "Settled deferred message must keep its sequence popped");
    }

    [TestMethod]
    public async Task HandleContinuationRequest_FromManager_Authorized()
    {
        var deferredCtx = CreateContext(messageType: MessageType.EventRequest, eventId: "event-1");
        var ctx = CreateContext(messageType: MessageType.ContinuationRequest, from: "Manager", eventId: "event-1");
        ctx.NextDeferredWithPopResult = deferredCtx;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, handler.HandleCalls);
    }

    [TestMethod]
    public async Task HandleContinuationRequest_FromUnauthorized_DeadLetters()
    {
        var ctx = CreateContext(messageType: MessageType.ContinuationRequest, from: "SomeEndpoint");
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.DeadLetterCalls);
    }

    [TestMethod]
    public async Task HandleContinuationRequest_NoNextDeferred_CompletesOnly()
    {
        var ctx = CreateContext(messageType: MessageType.ContinuationRequest, from: "Continuation", eventId: "event-1");
        ctx.NextDeferredWithPopResult = null; // no deferred message
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(0, handler.HandleCalls);
        Assert.AreEqual(1, ctx.CompletedCalls, "Should complete via NextDeferredException catch");
    }

    [TestMethod]
    public async Task HandleContinuationRequest_EventIdMismatch_CompletesOnly()
    {
        var deferredCtx = CreateContext(messageType: MessageType.EventRequest, eventId: "different-event");
        var ctx = CreateContext(messageType: MessageType.ContinuationRequest, from: "Continuation", eventId: "event-1");
        ctx.NextDeferredWithPopResult = deferredCtx;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(0, handler.HandleCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
        // The mismatched message was popped but never dispatched; its sequence must be
        // restored so a later drain can still reach it instead of orphaning it.
        Assert.AreEqual(1, ctx.RestoreNextDeferredCalls);
        Assert.AreSame(deferredCtx, ctx.LastRestoredDeferred);
    }

    [TestMethod]
    public async Task HandleContinuationRequest_HandlerThrowsOnDeferred_CompletesOriginal()
    {
        var deferredCtx = CreateContext(messageType: MessageType.EventRequest, eventId: "event-1");
        var ctx = CreateContext(messageType: MessageType.ContinuationRequest, from: "Continuation", eventId: "event-1");
        ctx.NextDeferredWithPopResult = deferredCtx;
        var handler = new FakeEventContextHandler { ThrowOnHandle = new InvalidOperationException("boom") };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        // Both deferred and continuation messages get completed
        Assert.AreEqual(1, deferredCtx.CompletedCalls, "Deferred message completed via error handling in HandleEventRequest");
        Assert.AreEqual(1, ctx.CompletedCalls, "Continuation message completed via EventContextHandlerException catch");
        Assert.AreEqual(0, ctx.RestoreNextDeferredCalls, "Settled deferred message must keep its sequence popped");
    }

    [TestMethod]
    public async Task HandleContinuationRequest_InboxCheckFails_RestoresDeferredAndAbandons()
    {
        // The sequence was popped before the nested dispatch; a store outage during the
        // inbox pre-check must put it back, or the deferred message stays broker-deferred
        // with no remaining reference and redelivery of the continuation cannot recover it.
        var deferredCtx = CreateContext(messageType: MessageType.EventRequest, eventId: "event-1");
        var ctx = CreateContext(messageType: MessageType.ContinuationRequest, from: "Continuation", eventId: "event-1");
        ctx.NextDeferredWithPopResult = deferredCtx;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(
            handler,
            response,
            inboxStore: new FakeInboxStore { CheckException = new InvalidOperationException("provider details") });

        await sut.Handle(ctx);

        Assert.AreEqual(0, handler.HandleCalls);
        Assert.AreEqual(0, deferredCtx.CompletedCalls, "Deferred message must stay unsettled");
        Assert.AreEqual(1, ctx.RestoreNextDeferredCalls, "Popped sequence must be restored for redelivery");
        Assert.AreSame(deferredCtx, ctx.LastRestoredDeferred);
        Assert.AreEqual(1, ctx.AbandonCalls, "Continuation must abandon for redelivery");
        Assert.AreEqual(0, ctx.CompletedCalls);
        Assert.AreEqual(0, ctx.DeadLetterCalls);
    }

    [TestMethod]
    public async Task HandleContinuationRequest_InboxRecordFails_RestoresDeferredAndAbandons()
    {
        // Record-on-success failure after the handler ran: the nested message is still
        // unsettled, so the popped sequence must be restored and the continuation abandoned
        // so the redelivery can re-run the (idempotent) handler and record again.
        var deferredCtx = CreateContext(messageType: MessageType.EventRequest, eventId: "event-1");
        var ctx = CreateContext(messageType: MessageType.ContinuationRequest, from: "Continuation", eventId: "event-1");
        ctx.NextDeferredWithPopResult = deferredCtx;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var store = new FakeInboxStore { RecordException = new InvalidOperationException("provider details") };
        // Production composition: record-only middleware at the handler seam plus the
        // pre-session-guard detector on StrictMessageHandler.
        var middleware = new NimBus.Core.Inbox.InboxMiddleware(handler, store, checkHandledUpstream: true);
        var sut = new StrictMessageHandler(
            middleware,
            response,
            NullLogger.Instance,
            retryPolicyProvider: null,
            pipeline: null,
            lifecycleNotifier: null,
            permanentFailureClassifier: null,
            failureDispositionClassifier: null,
            inboxDuplicateDetector: new NimBus.Core.Inbox.InboxDuplicateDetector(store));

        await sut.Handle(ctx);

        Assert.AreEqual(1, handler.HandleCalls, "Handler ran before the record failed");
        Assert.AreEqual(0, deferredCtx.CompletedCalls, "Deferred message must stay unsettled");
        Assert.AreEqual(0, response.ResolutionCalls, "No resolution may be published for an unrecorded success");
        Assert.AreEqual(1, ctx.RestoreNextDeferredCalls, "Popped sequence must be restored for redelivery");
        Assert.AreSame(deferredCtx, ctx.LastRestoredDeferred);
        Assert.AreEqual(1, ctx.AbandonCalls, "Continuation must abandon for redelivery");
        Assert.AreEqual(0, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleContinuationRequest_TransientHandlerFailure_RestoresDeferredAndAbandons()
    {
        var deferredCtx = CreateContext(messageType: MessageType.EventRequest, eventId: "event-1");
        var ctx = CreateContext(messageType: MessageType.ContinuationRequest, from: "Continuation", eventId: "event-1");
        ctx.NextDeferredWithPopResult = deferredCtx;
        var handler = new FakeEventContextHandler { ThrowOnHandle = new TransientException("transient") };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, handler.HandleCalls);
        Assert.AreEqual(0, deferredCtx.CompletedCalls, "Deferred message must stay unsettled");
        Assert.AreEqual(1, ctx.RestoreNextDeferredCalls, "Popped sequence must be restored for redelivery");
        Assert.AreEqual(1, ctx.AbandonCalls);
        Assert.AreEqual(0, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleContinuationRequest_CancellationAfterPop_RestoresDeferredWithFreshToken()
    {
        // Cancellation is itself one of the failure modes that leaves the popped message
        // unsettled. If the restore reuses the caller's already-cancelled token, the
        // session-state write cancels immediately, the best-effort catch swallows it, and
        // the deferred message is orphaned — exactly the loss the restore exists to prevent.
        var deferredCtx = CreateContext(messageType: MessageType.EventRequest, eventId: "event-1");
        var ctx = CreateContext(messageType: MessageType.ContinuationRequest, from: "Continuation", eventId: "event-1");
        ctx.NextDeferredWithPopResult = deferredCtx;
        using var cancellation = new CancellationTokenSource();
        var handler = new FakeEventContextHandler
        {
            OnHandle = _ =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            },
        };
        var sut = CreateHandler(handler, new FakeResponseService());

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => sut.Handle(ctx, cancellation.Token));

        Assert.AreEqual(0, deferredCtx.CompletedCalls, "Deferred message must stay unsettled");
        Assert.AreEqual(1, ctx.RestoreNextDeferredCalls, "Restore must still run when the caller token is already cancelled");
        Assert.IsFalse(ctx.LastRestoreToken.IsCancellationRequested, "Restore must run under a fresh bounded token, not the cancelled caller token");
        Assert.AreEqual(0, ctx.CompletedCalls, "Cancellation must propagate to the transport without settling");
    }

    // HandleProcessDeferredRequest tests removed — deferred processing
    // is now handled by a separate DeferredProcessorFunction in subscriber apps,
    // not by StrictMessageHandler.

    // ── MessageType routing ─────────────────────────────────────────────

    [TestMethod]
    public async Task Handle_UnsupportedResponse_RoutesToHandleEventRequest()
    {
        // UnsupportedResponse is routed to HandleEventRequest in the base MessageHandler
        var ctx = CreateContext(messageType: MessageType.UnsupportedResponse);
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, handler.HandleCalls);
        Assert.AreEqual(1, response.ResolutionCalls);
    }

    [TestMethod]
    public async Task Handle_UnhandledMessageType_DeadLetters()
    {
        var ctx = CreateContext(messageType: MessageType.ErrorResponse);
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        // ErrorResponse falls through to HandleDefault which throws UnsupportedMessageTypeException -> dead-letter
        Assert.AreEqual(1, ctx.DeadLetterCalls);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    // ── Inbox pre-check (duplicate detection before session guards) ──────

    [TestMethod]
    public async Task HandleRetryRequest_RecordedDuplicate_WithUnblockedSession_SendsDuplicateResponse()
    {
        // A successfully handled RetryRequest leaves its session unblocked; without the
        // pre-check its redelivery would fail VerifySessionIsBlockedByThis and complete
        // with a normal resolution response, hiding the duplicate.
        var ctx = CreateContext(messageType: MessageType.RetryRequest);
        ctx.IsSessionBlockedByThisResult = false;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response, inboxStore: new FakeInboxStore { HasProcessed = true });

        await sut.Handle(ctx);

        Assert.AreEqual(0, handler.HandleCalls);
        Assert.AreEqual(1, response.DuplicateCalls);
        Assert.AreEqual(0, response.ResolutionCalls);
        Assert.AreEqual(0, ctx.UnblockSessionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
        Assert.AreEqual(HandlerOutcome.DuplicateDetected, ctx.HandlerOutcome);
    }

    [TestMethod]
    public async Task HandleRetryRequest_RecordedDuplicate_WithSessionStillBlockedByThis_UnblocksBeforeCompleting()
    {
        // Crash window: the first attempt recorded the inbox entry but crashed before
        // unblocking. The duplicate path must still release the session and drain deferred
        // siblings, or the session would stay blocked forever.
        var ctx = CreateContext(messageType: MessageType.RetryRequest);
        ctx.IsSessionBlockedByThisResult = true;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response, inboxStore: new FakeInboxStore { HasProcessed = true });

        await sut.Handle(ctx);

        Assert.AreEqual(0, handler.HandleCalls);
        Assert.AreEqual(1, ctx.UnblockSessionCalls);
        Assert.AreEqual(1, response.DuplicateCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_RecordedDuplicate_WhileSessionBlockedByOther_SkipsInsteadOfDeferring()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        ctx.BlockedByEventId = "other-event";
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response, inboxStore: new FakeInboxStore { HasProcessed = true });

        await sut.Handle(ctx);

        Assert.AreEqual(0, handler.HandleCalls);
        Assert.AreEqual(1, response.DuplicateCalls);
        Assert.AreEqual(0, response.DeferralCalls);
        Assert.AreEqual(0, response.SendToDeferredSubscriptionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_PreCheckStoreFailure_AbandonsWithoutRunningHandler()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(
            handler,
            response,
            inboxStore: new FakeInboxStore { CheckException = new InvalidOperationException("provider details") });

        await sut.Handle(ctx);

        Assert.AreEqual(0, handler.HandleCalls);
        Assert.AreEqual(1, ctx.AbandonCalls);
        Assert.AreEqual(0, ctx.CompletedCalls);
        Assert.AreEqual(0, ctx.DeadLetterCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_NotRecorded_PreCheckAllowsNormalProcessing()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response, inboxStore: new FakeInboxStore());

        await sut.Handle(ctx);

        Assert.AreEqual(1, handler.HandleCalls);
        Assert.AreEqual(1, response.ResolutionCalls);
        Assert.AreEqual(0, response.DuplicateCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleRetryRequest_RecordedDuplicate_WithUnblockedSession_DrainsDeferredSiblings()
    {
        // Crash window: the first attempt recorded and unblocked, then died before the
        // deferred drain. The redelivered duplicate is the only remaining drain trigger —
        // completing it without draining would park deferred siblings indefinitely.
        var ctx = CreateContext(messageType: MessageType.RetryRequest);
        ctx.IsSessionBlockedByThisResult = false;
        ctx.BlockedByEventId = null;
        ctx.DeferredCountResult = 2;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response, inboxStore: new FakeInboxStore { HasProcessed = true });

        await sut.Handle(ctx);

        Assert.AreEqual(0, handler.HandleCalls);
        Assert.AreEqual(0, ctx.UnblockSessionCalls);
        Assert.AreEqual(1, response.ProcessDeferredCalls);
        Assert.AreEqual(1, response.DuplicateCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleRetryRequest_RecordedDuplicate_WithUnblockedSession_DrainsLegacyDeferredSibling()
    {
        var deferredCtx = CreateContext(messageType: MessageType.EventRequest, eventId: "deferred-event");
        var ctx = CreateContext(messageType: MessageType.RetryRequest);
        ctx.IsSessionBlockedByThisResult = false;
        ctx.BlockedByEventId = null;
        ctx.NextDeferredResult = deferredCtx;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response, inboxStore: new FakeInboxStore { HasProcessed = true });

        await sut.Handle(ctx);

        Assert.AreEqual(0, handler.HandleCalls);
        Assert.AreEqual(1, response.ContinuationCalls);
        Assert.AreEqual(1, response.DuplicateCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleRetryRequest_RecordedDuplicate_WithSessionBlockedByOther_LeavesBlockerAndSkipsDrain()
    {
        // An unrelated later event owns the session and its eventual settlement owns the
        // drain; the duplicate must neither unblock nor trigger it early.
        var ctx = CreateContext(messageType: MessageType.RetryRequest);
        ctx.IsSessionBlockedByThisResult = false;
        ctx.BlockedByEventId = "other-event";
        ctx.DeferredCountResult = 2;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response, inboxStore: new FakeInboxStore { HasProcessed = true });

        await sut.Handle(ctx);

        Assert.AreEqual(0, handler.HandleCalls);
        Assert.AreEqual(0, ctx.UnblockSessionCalls);
        Assert.AreEqual(0, response.ProcessDeferredCalls);
        Assert.AreEqual(0, response.ContinuationCalls);
        Assert.AreEqual(1, response.DuplicateCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleResubmissionRequest_RecordedDuplicate_PreCheckSkipsHandler()
    {
        // The handler-seam decorator is record-only in hosted compositions, so the
        // resubmission entry point must run the pre-check itself.
        var ctx = CreateContext(messageType: MessageType.ResubmissionRequest, from: "Manager");
        ctx.DeferredCountResult = 1;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response, inboxStore: new FakeInboxStore { HasProcessed = true });

        await sut.Handle(ctx);

        Assert.AreEqual(0, handler.HandleCalls);
        Assert.AreEqual(1, response.DuplicateCalls);
        Assert.AreEqual(0, response.ResolutionCalls);
        // Mirrors the normal resubmission path: no unblock when not blocked by this,
        // and the unconditional deferred drain still runs.
        Assert.AreEqual(0, ctx.UnblockSessionCalls);
        Assert.AreEqual(1, response.ProcessDeferredCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    private static StrictMessageHandler CreateHandler(
        FakeEventContextHandler handler,
        FakeResponseService response,
        FakeDeferredMessageProcessor processor = null,
        string topicName = null,
        NimBus.Core.Inbox.IInboxStore inboxStore = null)
    {
        if (inboxStore is null)
            return new StrictMessageHandler(handler, response, NullLogger.Instance);

        return new StrictMessageHandler(
            handler,
            response,
            NullLogger.Instance,
            retryPolicyProvider: null,
            pipeline: null,
            lifecycleNotifier: null,
            permanentFailureClassifier: null,
            failureDispositionClassifier: null,
            inboxDuplicateDetector: new NimBus.Core.Inbox.InboxDuplicateDetector(inboxStore));
    }

    private sealed class FakeInboxStore : NimBus.Core.Inbox.IInboxStore
    {
        public bool HasProcessed { get; set; }
        public Exception CheckException { get; set; }
        public Exception RecordException { get; set; }

        public Task<bool> HasProcessedAsync(
            string endpointId,
            string messageId,
            CancellationToken cancellationToken = default)
        {
            if (CheckException != null)
                throw CheckException;
            return Task.FromResult(HasProcessed);
        }

        public Task RecordProcessedAsync(
            string endpointId,
            string messageId,
            CancellationToken cancellationToken = default)
        {
            if (RecordException != null)
                throw RecordException;
            return Task.CompletedTask;
        }

        public Task<int> PurgeExpiredAsync(
            string endpointId,
            DateTimeOffset olderThan,
            CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private static FakeMessageContext CreateContext(
        MessageType messageType,
        string from = "StorefrontEndpoint",
        string eventId = "event-1",
        string eventTypeId = "OrderPlaced")
    {
        return new FakeMessageContext
        {
            EventId = eventId,
            MessageId = "message-1",
            CorrelationId = "correlation-1",
            SessionId = "session-1",
            ParentMessageId = "self",
            OriginatingMessageId = "self",
            OriginatingFrom = from,
            From = from,
            To = "AnalyticsEndpoint",
            MessageType = messageType,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = eventTypeId,
                    EventJson = "{}",
                },
            },
            EventTypeId = eventTypeId,
            EnqueuedTimeUtc = new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc),
        };
    }

    // ── Fakes ────────────────────────────────────────────────────────────

    private sealed class FakeEventContextHandler : IEventContextHandler
    {
        public int HandleCalls { get; private set; }
        public Exception ThrowOnHandle { get; set; }
        public Action<IMessageContext> OnHandle { get; set; }

        public Task Handle(IMessageContext context, CancellationToken cancellationToken = default)
        {
            HandleCalls++;
            OnHandle?.Invoke(context);
            if (ThrowOnHandle != null)
                throw ThrowOnHandle;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeResponseService : IResponseService
    {
        public int ResolutionCalls { get; private set; }
        public int ErrorCalls { get; private set; }
        public int DeferralCalls { get; private set; }
        public int SkipCalls { get; private set; }
        public int RetryCalls { get; private set; }
        public int UnsupportedCalls { get; private set; }
        public int ContinuationCalls { get; private set; }
        public int SendToDeferredSubscriptionCalls { get; private set; }
        public int ProcessDeferredCalls { get; private set; }
        public int DeadLetterCalls { get; private set; }
        public int DiscardCalls { get; private set; }
        public int PendingHandoffCalls { get; private set; }
        public int DuplicateCalls { get; private set; }
        public HandoffMetadata LastPendingHandoffMetadata { get; private set; }
        public string LastDeadLetterReason { get; private set; }
        public Exception LastDeadLetterException { get; private set; }
        public Exception LastErrorException { get; private set; }
        public Exception LastDiscardException { get; private set; }
        public string LastDiscardClassifierName { get; private set; }
        public int? LastRetryDelayMinutes { get; private set; }

        public Task SendResolutionResponse(IMessageContext mc, CancellationToken ct = default) { ResolutionCalls++; return Task.CompletedTask; }
        public Task SendSkipResponse(IMessageContext mc, CancellationToken ct = default) { SkipCalls++; return Task.CompletedTask; }
        public Task SendDuplicateResponse(IMessageContext mc, CancellationToken ct = default) { DuplicateCalls++; return Task.CompletedTask; }
        public Task SendDiscardResponse(IMessageContext mc, Exception ex, string classifierName, CancellationToken ct = default)
        {
            DiscardCalls++;
            LastDiscardException = ex;
            LastDiscardClassifierName = classifierName;
            return Task.CompletedTask;
        }
        public Task SendErrorResponse(IMessageContext mc, Exception ex, CancellationToken ct = default) { ErrorCalls++; LastErrorException = ex; return Task.CompletedTask; }
        public Task SendDeadLetterResponse(IMessageContext mc, string reason, Exception ex, CancellationToken ct = default)
        {
            DeadLetterCalls++;
            LastDeadLetterReason = reason;
            LastDeadLetterException = ex;
            return Task.CompletedTask;
        }
        public Task SendDeferralResponse(IMessageContext mc, SessionBlockedException ex, CancellationToken ct = default) { DeferralCalls++; return Task.CompletedTask; }
        public Task SendRetryResponse(IMessageContext mc, int delay, CancellationToken ct = default) { RetryCalls++; LastRetryDelayMinutes = delay; return Task.CompletedTask; }
        public Task SendUnsupportedResponse(IMessageContext mc, CancellationToken ct = default) { UnsupportedCalls++; return Task.CompletedTask; }
        public Task SendContinuationRequestToSelf(IMessageContext mc, CancellationToken ct = default) { ContinuationCalls++; return Task.CompletedTask; }
        public Task SendToDeferredSubscription(IMessageContext mc, int seq, CancellationToken ct = default) { SendToDeferredSubscriptionCalls++; return Task.CompletedTask; }
        public Task SendProcessDeferredRequest(IMessageContext mc, CancellationToken ct = default) { ProcessDeferredCalls++; return Task.CompletedTask; }
        public Task SendPendingHandoffResponse(IMessageContext mc, HandoffMetadata handoff, CancellationToken ct = default)
        {
            PendingHandoffCalls++;
            LastPendingHandoffMetadata = handoff;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRetryPolicyProvider : IRetryPolicyProvider
    {
        public int GetRetryPolicyCalls { get; private set; }
        public string LastEventTypeId { get; private set; }
        public RetryPolicy PolicyToReturn { get; set; }

        public RetryPolicy GetRetryPolicy(string eventTypeId, string exceptionMessage, string endpoint = null)
        {
            GetRetryPolicyCalls++;
            LastEventTypeId = eventTypeId;
            return PolicyToReturn;
        }
    }

    private sealed class FakeFailureDispositionClassifier : IFailureDispositionClassifier
    {
        private readonly FailureDisposition _disposition;

        public FakeFailureDispositionClassifier(FailureDisposition disposition)
        {
            _disposition = disposition;
        }

        public Exception LastException { get; private set; }
        public string LastEventTypeId { get; private set; }
        public string? LastEndpointName { get; private set; }

        public FailureDisposition Classify(Exception exception, string eventTypeId, string? endpointName)
        {
            LastException = exception;
            LastEventTypeId = eventTypeId;
            LastEndpointName = endpointName;
            return _disposition;
        }
    }

#pragma warning disable CS0618
    private sealed class AlwaysPermanentFailureClassifier : IPermanentFailureClassifier
    {
        public bool IsPermanentFailure(Exception exception) => true;
    }
#pragma warning restore CS0618

    private sealed class RecordingLifecycleObserver : IMessageLifecycleObserver
    {
        public int ReceivedCalls { get; private set; }
        public int CompletedCalls { get; private set; }
        public int FailedCalls { get; private set; }

        public Task OnMessageReceived(MessageLifecycleContext context, CancellationToken cancellationToken = default)
        {
            ReceivedCalls++;
            return Task.CompletedTask;
        }

        public Task OnMessageCompleted(MessageLifecycleContext context, CancellationToken cancellationToken = default)
        {
            CompletedCalls++;
            return Task.CompletedTask;
        }

        public Task OnMessageFailed(MessageLifecycleContext context, Exception exception, CancellationToken cancellationToken = default)
        {
            FailedCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDeferredMessageProcessor : IDeferredMessageProcessor
    {
        public int ProcessCalls { get; private set; }
        public string LastSessionId { get; private set; }
        public string LastTopicName { get; private set; }

        public Task ProcessDeferredMessagesAsync(string sessionId, string topicName, CancellationToken ct = default)
        {
            ProcessCalls++;
            LastSessionId = sessionId;
            LastTopicName = topicName;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMessageContext : IMessageContext
    {
        public string EventId { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string CorrelationId { get; set; } = string.Empty;
        public string MessageId { get; set; } = string.Empty;
        public MessageType MessageType { get; set; }
        public MessageContent MessageContent { get; set; } = new();
        public string ParentMessageId { get; set; } = string.Empty;
        public string OriginatingMessageId { get; set; } = string.Empty;
        public int? RetryCount { get; set; }
        public string OriginatingFrom { get; set; } = string.Empty;
        public string EventTypeId { get; set; } = string.Empty;
        public string OriginalSessionId { get; set; } = string.Empty;
        public int? DeferralSequence { get; set; }
        public DateTime EnqueuedTimeUtc { get; set; }
        public string From { get; set; } = string.Empty;
        public string DeadLetterReason { get; set; }
        public string DeadLetterErrorDescription { get; set; }
        public string HandoffReason { get; set; }
        public string ExternalJobId { get; set; }
        public DateTime? ExpectedBy { get; set; }
        public bool IsDeferred { get; set; }
        public int ThrottleRetryCount { get; set; }
        public long? QueueTimeMs { get; set; }
        public long? ProcessingTimeMs { get; set; }
        public DateTime? HandlerStartedAtUtc { get; set; }
        public HandlerOutcome HandlerOutcome { get; set; }
        public HandoffMetadata HandoffMetadata { get; set; }

        // Configurable behavior
        public string BlockedByEventId { get; set; }
        public bool IsSessionBlockedByThisResult { get; set; }
        public IMessageContext NextDeferredResult { get; set; }
        public IMessageContext NextDeferredWithPopResult { get; set; }
        public int DeferredCountResult { get; set; }

        // Call counters
        public int CompletedCalls { get; private set; }
        public int DeadLetterCalls { get; private set; }
        public int AbandonCalls { get; private set; }
        public int BlockSessionCalls { get; private set; }
        public int UnblockSessionCalls { get; private set; }
        public int IncrementDeferredCountCalls { get; private set; }
        public int ResetDeferredCountCalls { get; private set; }
        public int RestoreNextDeferredCalls { get; private set; }
        public IMessageContext LastRestoredDeferred { get; private set; }
        public CancellationToken LastRestoreToken { get; private set; }

        public Task Complete(CancellationToken ct = default) { CompletedCalls++; return Task.CompletedTask; }
        public Task Abandon(TransientException ex) { AbandonCalls++; return Task.CompletedTask; }
        public Task DeadLetter(string reason, Exception ex = null, CancellationToken ct = default) { DeadLetterCalls++; return Task.CompletedTask; }
        public Task Defer(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeferOnly(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IMessageContext> ReceiveNextDeferred(CancellationToken ct = default) => Task.FromResult(NextDeferredResult);
        public Task<IMessageContext> ReceiveNextDeferredWithPop(CancellationToken ct = default) => Task.FromResult(NextDeferredWithPopResult);
        public Task RestoreNextDeferred(IMessageContext deferredMessage, CancellationToken ct = default)
        {
            // Mirror the real transport contexts: restoring writes session state, and that
            // I/O observes the token before doing anything.
            ct.ThrowIfCancellationRequested();
            RestoreNextDeferredCalls++;
            LastRestoredDeferred = deferredMessage;
            LastRestoreToken = ct;
            return Task.CompletedTask;
        }
        public Task BlockSession(CancellationToken ct = default) { BlockSessionCalls++; return Task.CompletedTask; }
        public Task UnblockSession(CancellationToken ct = default) { UnblockSessionCalls++; return Task.CompletedTask; }
        public Task<bool> IsSessionBlocked(CancellationToken ct = default) => Task.FromResult(!string.IsNullOrEmpty(BlockedByEventId));
        public Task<bool> IsSessionBlockedByThis(CancellationToken ct = default) => Task.FromResult(IsSessionBlockedByThisResult);
        public Task<bool> IsSessionBlockedByEventId(CancellationToken ct = default) => Task.FromResult(!string.IsNullOrEmpty(BlockedByEventId));
        public Task<string> GetBlockedByEventId(CancellationToken ct = default) => Task.FromResult(BlockedByEventId);
        public Task<int> GetNextDeferralSequenceAndIncrement(CancellationToken ct = default) => Task.FromResult(0);
        public Task IncrementDeferredCount(CancellationToken ct = default) { IncrementDeferredCountCalls++; return Task.CompletedTask; }
        public Task DecrementDeferredCount(CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> GetDeferredCount(CancellationToken ct = default) => Task.FromResult(DeferredCountResult);
        public Task<bool> HasDeferredMessages(CancellationToken ct = default) => Task.FromResult(NextDeferredResult != null || DeferredCountResult > 0);
        public Task ResetDeferredCount(CancellationToken ct = default) { ResetDeferredCountCalls++; return Task.CompletedTask; }
        public Task ScheduleRedelivery(TimeSpan delay, int throttleRetryCount, CancellationToken ct = default) => Task.CompletedTask;
    }
}
