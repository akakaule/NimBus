#pragma warning disable CA1707, CA1515, CA2007
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;

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
    public async Task HandleEventRequest_Heartbeat_SkipsHandlerAndCompletes()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest, eventTypeId: "Heartbeat");
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(0, handler.HandleCalls, "Should skip event handler for Heartbeat");
        Assert.AreEqual(1, response.ResolutionCalls);
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
    public async Task HandleHandoffCompletedRequest_NotBlockedByThis_DoesNothing()
    {
        var ctx = CreateContext(messageType: MessageType.HandoffCompletedRequest, from: "Manager");
        ctx.IsSessionBlockedByThisResult = false;
        ctx.BlockedByEventId = "different-event";
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        // VerifySessionIsBlockedByThis throws SessionBlockedException, which the
        // base MessageHandler swallows. No state changes — the message is left
        // for redelivery (per the existing pattern for other control flows).
        Assert.AreEqual(0, ctx.UnblockSessionCalls);
        Assert.AreEqual(0, response.ResolutionCalls);
        Assert.AreEqual(0, ctx.CompletedCalls);
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

    private static StrictMessageHandler CreateHandler(
        FakeEventContextHandler handler,
        FakeResponseService response,
        FakeDeferredMessageProcessor processor = null,
        string topicName = null)
    {
        return new StrictMessageHandler(handler, response, NullLogger.Instance);
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
        public int PendingHandoffCalls { get; private set; }
        public HandoffMetadata LastPendingHandoffMetadata { get; private set; }
        public string LastDeadLetterReason { get; private set; }
        public Exception LastDeadLetterException { get; private set; }
        public Exception LastErrorException { get; private set; }
        public int? LastRetryDelayMinutes { get; private set; }

        public Task SendResolutionResponse(IMessageContext mc, CancellationToken ct = default) { ResolutionCalls++; return Task.CompletedTask; }
        public Task SendSkipResponse(IMessageContext mc, CancellationToken ct = default) { SkipCalls++; return Task.CompletedTask; }
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

        public Task Complete(CancellationToken ct = default) { CompletedCalls++; return Task.CompletedTask; }
        public Task Abandon(TransientException ex) { AbandonCalls++; return Task.CompletedTask; }
        public Task DeadLetter(string reason, Exception ex = null, CancellationToken ct = default) { DeadLetterCalls++; return Task.CompletedTask; }
        public Task Defer(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeferOnly(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IMessageContext> ReceiveNextDeferred(CancellationToken ct = default) => Task.FromResult(NextDeferredResult);
        public Task<IMessageContext> ReceiveNextDeferredWithPop(CancellationToken ct = default) => Task.FromResult(NextDeferredWithPopResult);
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
