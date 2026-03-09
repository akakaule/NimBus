#pragma warning disable CA1707, CA1515, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Logging;
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

    // ── HandleProcessDeferredRequest ────────────────────────────────────

    [TestMethod]
    public async Task HandleProcessDeferredRequest_WithProcessor_ProcessesAndCompletes()
    {
        var ctx = CreateContext(messageType: MessageType.ProcessDeferredRequest, from: "DeferredProcessor");
        ctx.SessionId = "session-1";
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var processor = new FakeDeferredMessageProcessor();
        var sut = CreateHandler(handler, response, processor, "my-topic");

        await sut.Handle(ctx);

        Assert.AreEqual(1, processor.ProcessCalls);
        Assert.AreEqual("session-1", processor.LastSessionId);
        Assert.AreEqual("my-topic", processor.LastTopicName);
        Assert.AreEqual(1, ctx.ResetDeferredCountCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleProcessDeferredRequest_NoProcessor_DeadLetters()
    {
        var ctx = CreateContext(messageType: MessageType.ProcessDeferredRequest, from: "DeferredProcessor");
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        // Use 3-arg constructor (no processor)
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        // InvalidOperationException is caught by base MessageHandler -> dead-letter
        Assert.AreEqual(1, ctx.DeadLetterCalls);
    }

    [TestMethod]
    public async Task HandleProcessDeferredRequest_NoTopicName_DeadLetters()
    {
        var ctx = CreateContext(messageType: MessageType.ProcessDeferredRequest, from: "DeferredProcessor");
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var processor = new FakeDeferredMessageProcessor();
        var sut = CreateHandler(handler, response, processor, topicName: "");

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.DeadLetterCalls);
    }

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
        var logger = new FakeLoggerProvider();
        if (processor != null)
            return new StrictMessageHandler(handler, response, logger, processor, topicName);
        return new StrictMessageHandler(handler, response, logger);
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

    private sealed class FakeLoggerProvider : ILoggerProvider
    {
        private readonly ILogger _logger = new FakeLogger();
        public ILogger GetContextualLogger(IMessageContext messageContext) => _logger;
        public ILogger GetContextualLogger(IMessage message) => _logger;
        public ILogger GetContextualLogger(string correlationId) => _logger;
    }

    private sealed class FakeLogger : ILogger
    {
        public void Verbose(string messageTemplate, params object[] propertyValues) { }
        public void Verbose(Exception exception, string messageTemplate, params object[] propertyValues) { }
        public void Information(string messageTemplate, params object[] propertyValues) { }
        public void Information(Exception exception, string messageTemplate, params object[] propertyValues) { }
        public void Error(string messageTemplate, params object[] propertyValues) { }
        public void Error(Exception exception, string messageTemplate, params object[] propertyValues) { }
        public void Fatal(string messageTemplate, params object[] propertyValues) { }
        public void Fatal(Exception exception, string messageTemplate, params object[] propertyValues) { }
    }

    private sealed class FakeEventContextHandler : IEventContextHandler
    {
        public int HandleCalls { get; private set; }
        public Exception ThrowOnHandle { get; set; }

        public Task Handle(IMessageContext context, ILogger logger, CancellationToken cancellationToken = default)
        {
            HandleCalls++;
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
        public int? LastRetryDelayMinutes { get; private set; }

        public Task SendResolutionResponse(IMessageContext mc, CancellationToken ct = default) { ResolutionCalls++; return Task.CompletedTask; }
        public Task SendSkipResponse(IMessageContext mc, CancellationToken ct = default) { SkipCalls++; return Task.CompletedTask; }
        public Task SendErrorResponse(IMessageContext mc, Exception ex, CancellationToken ct = default) { ErrorCalls++; return Task.CompletedTask; }
        public Task SendDeferralResponse(IMessageContext mc, SessionBlockedException ex, CancellationToken ct = default) { DeferralCalls++; return Task.CompletedTask; }
        public Task SendRetryResponse(IMessageContext mc, int delay, CancellationToken ct = default) { RetryCalls++; LastRetryDelayMinutes = delay; return Task.CompletedTask; }
        public Task SendUnsupportedResponse(IMessageContext mc, CancellationToken ct = default) { UnsupportedCalls++; return Task.CompletedTask; }
        public Task SendContinuationRequestToSelf(IMessageContext mc, CancellationToken ct = default) { ContinuationCalls++; return Task.CompletedTask; }
        public Task SendToDeferredSubscription(IMessageContext mc, int seq, CancellationToken ct = default) { SendToDeferredSubscriptionCalls++; return Task.CompletedTask; }
        public Task SendProcessDeferredRequest(IMessageContext mc, CancellationToken ct = default) { ProcessDeferredCalls++; return Task.CompletedTask; }
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
        public bool IsDeferred { get; set; }
        public int ThrottleRetryCount { get; set; }

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
