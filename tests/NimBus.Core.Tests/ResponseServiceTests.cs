#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Tests;

[TestClass]
public class ResponseServiceTests
{
    // ── SendResolutionResponse ──────────────────────────────────────────

    [TestMethod]
    public async Task SendResolutionResponse_RoutesToResolver()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext();

        await sut.SendResolutionResponse(ctx);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(Constants.ResolverId, msg.To);
        Assert.AreEqual(MessageType.ResolutionResponse, msg.MessageType);
    }

    [TestMethod]
    public async Task SendResolutionResponse_CopiesMessageContentFromContext()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var content = new MessageContent { EventContent = new EventContent { EventJson = "{\"x\":1}" } };
        var ctx = CreateContext(messageContent: content);

        await sut.SendResolutionResponse(ctx);

        var msg = sender.SentMessages.Single();
        Assert.AreSame(content, msg.MessageContent);
    }

    // ── SendSkipResponse ────────────────────────────────────────────────

    [TestMethod]
    public async Task SendSkipResponse_RoutesToResolverWithEmptyContent()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);

        await sut.SendSkipResponse(CreateContext());

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(Constants.ResolverId, msg.To);
        Assert.AreEqual(MessageType.SkipResponse, msg.MessageType);
        Assert.IsNotNull(msg.MessageContent);
    }

    // ── SendErrorResponse ───────────────────────────────────────────────

    [TestMethod]
    public async Task SendErrorResponse_RoutesToResolverWithErrorContent()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ex = new InvalidOperationException("boom");

        await sut.SendErrorResponse(CreateContext(), ex);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(Constants.ResolverId, msg.To);
        Assert.AreEqual(MessageType.ErrorResponse, msg.MessageType);
        Assert.AreEqual("boom", msg.MessageContent.ErrorContent.ErrorText);
        Assert.AreEqual(typeof(InvalidOperationException).FullName, msg.MessageContent.ErrorContent.ErrorType);
    }

    // ── SendDeferralResponse ────────────────────────────────────────────

    [TestMethod]
    public async Task SendDeferralResponse_RoutesToResolverWithDeferralType()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ex = new SessionBlockedException("blocked");

        await sut.SendDeferralResponse(CreateContext(), ex);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(Constants.ResolverId, msg.To);
        Assert.AreEqual(MessageType.DeferralResponse, msg.MessageType);
        Assert.AreEqual("blocked", msg.MessageContent.ErrorContent.ErrorText);
    }

    // ── SendRetryResponse ───────────────────────────────────────────────

    [TestMethod]
    public async Task SendRetryResponse_RoutesToRetryWithIncrementedCount()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(retryCount: 2);

        await sut.SendRetryResponse(ctx, 5);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(Constants.RetryId, msg.To);
        Assert.AreEqual(MessageType.RetryRequest, msg.MessageType);
        Assert.AreEqual(3, msg.RetryCount, "RetryCount should be incremented by 1");
        Assert.AreEqual(5, sender.LastDelay, "Delay should be forwarded");
    }

    [TestMethod]
    public async Task SendRetryResponse_NullRetryCount_StartsAtOne()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(retryCount: null);

        await sut.SendRetryResponse(ctx, 1);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(1, msg.RetryCount);
    }

    // ── SendUnsupportedResponse ─────────────────────────────────────────

    [TestMethod]
    public async Task SendUnsupportedResponse_RoutesToResolverWithUnsupportedType()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);

        await sut.SendUnsupportedResponse(CreateContext());

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(Constants.ResolverId, msg.To);
        Assert.AreEqual(MessageType.UnsupportedResponse, msg.MessageType);
    }

    // ── SendContinuationRequestToSelf ───────────────────────────────────

    [TestMethod]
    public async Task SendContinuationRequestToSelf_RoutesToContinuation()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);

        await sut.SendContinuationRequestToSelf(CreateContext());

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(Constants.ContinuationId, msg.To);
        Assert.AreEqual(MessageType.ContinuationRequest, msg.MessageType);
    }

    [TestMethod]
    public async Task SendContinuationRequestToSelf_OriginatingMessageIdSelf_UsesMessageId()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(originatingMessageId: Constants.Self, messageId: "msg-1");

        await sut.SendContinuationRequestToSelf(ctx);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual("msg-1", msg.OriginatingMessageId,
            "When OriginatingMessageId is 'self', should use MessageId instead");
    }

    [TestMethod]
    public async Task SendContinuationRequestToSelf_OriginatingMessageIdNotSelf_PreservesOriginal()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(originatingMessageId: "orig-msg-1", messageId: "msg-1");

        await sut.SendContinuationRequestToSelf(ctx);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual("orig-msg-1", msg.OriginatingMessageId);
    }

    // ── SendToDeferredSubscription ──────────────────────────────────────

    [TestMethod]
    public async Task SendToDeferredSubscription_RoutesToDeferredWithSequenceAndSessionId()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(sessionId: "session-42");

        await sut.SendToDeferredSubscription(ctx, 7);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(Constants.DeferredSubscriptionName, msg.To);
        Assert.AreEqual(7, msg.DeferralSequence);
        Assert.AreEqual("session-42", msg.OriginalSessionId);
        Assert.AreEqual("session-42", msg.SessionId);
    }

    [TestMethod]
    public async Task SendToDeferredSubscription_PreservesMessageTypeFromContext()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(messageType: MessageType.EventRequest);

        await sut.SendToDeferredSubscription(ctx, 1);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(MessageType.EventRequest, msg.MessageType,
            "Should preserve original message type, not set a new one");
    }

    [TestMethod]
    public async Task SendToDeferredSubscription_UsesCorrelationIdFromContext()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(correlationId: "corr-99");

        await sut.SendToDeferredSubscription(ctx, 1);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual("corr-99", msg.CorrelationId);
    }

    // ── SendProcessDeferredRequest ──────────────────────────────────────

    [TestMethod]
    public async Task SendProcessDeferredRequest_RoutesToDeferredProcessor()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);

        await sut.SendProcessDeferredRequest(CreateContext());

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(Constants.DeferredProcessorId, msg.To);
        Assert.AreEqual(MessageType.ProcessDeferredRequest, msg.MessageType);
    }

    // ── Common property preservation ────────────────────────────────────

    [TestMethod]
    public async Task CreateResponse_SetsCorrelationIdToMessageId()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(messageId: "msg-42");

        await sut.SendResolutionResponse(ctx);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual("msg-42", msg.CorrelationId);
    }

    [TestMethod]
    public async Task CreateResponse_PreservesSessionId()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(sessionId: "session-42");

        await sut.SendResolutionResponse(ctx);

        Assert.AreEqual("session-42", sender.SentMessages.Single().SessionId);
    }

    [TestMethod]
    public async Task CreateResponse_SetsParentMessageIdToContextMessageId()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(messageId: "msg-99");

        await sut.SendResolutionResponse(ctx);

        Assert.AreEqual("msg-99", sender.SentMessages.Single().ParentMessageId);
    }

    [TestMethod]
    public async Task CreateResponse_OriginatingMessageIdSelf_FallsBackToMessageId()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(originatingMessageId: Constants.Self, messageId: "msg-1");

        await sut.SendResolutionResponse(ctx);

        Assert.AreEqual("msg-1", sender.SentMessages.Single().OriginatingMessageId);
    }

    [TestMethod]
    public async Task CreateResponse_OriginatingMessageIdNotSelf_PreservesOriginal()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(originatingMessageId: "orig-1");

        await sut.SendResolutionResponse(ctx);

        Assert.AreEqual("orig-1", sender.SentMessages.Single().OriginatingMessageId);
    }

    [TestMethod]
    public async Task CreateResponse_PreservesOriginatingFrom()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(from: "BillingEndpoint");

        await sut.SendResolutionResponse(ctx);

        Assert.AreEqual("BillingEndpoint", sender.SentMessages.Single().OriginatingFrom);
    }

    // ── Constructor ─────────────────────────────────────────────────────

    [TestMethod]
    public void Constructor_NullSender_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new ResponseService(null!));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static FakeMessageContext CreateContext(
        string messageId = "message-1",
        string sessionId = "session-1",
        string eventId = "event-1",
        string correlationId = "correlation-1",
        string from = "StorefrontEndpoint",
        string originatingMessageId = "originating-1",
        int? retryCount = null,
        MessageType messageType = MessageType.EventRequest,
        MessageContent messageContent = null)
    {
        return new FakeMessageContext
        {
            MessageId = messageId,
            SessionId = sessionId,
            EventId = eventId,
            CorrelationId = correlationId,
            From = from,
            OriginatingMessageId = originatingMessageId,
            OriginatingFrom = from,
            ParentMessageId = "self",
            To = "AnalyticsEndpoint",
            RetryCount = retryCount,
            MessageType = messageType,
            EventTypeId = "OrderPlaced",
            MessageContent = messageContent ?? new MessageContent
            {
                EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" }
            },
        };
    }

    // ── Fakes ────────────────────────────────────────────────────────────

    private sealed class RecordingSender : ISender
    {
        public List<IMessage> SentMessages { get; } = new();
        public int LastDelay { get; private set; }

        public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            SentMessages.Add(message);
            LastDelay = messageEnqueueDelay;
            return Task.CompletedTask;
        }

        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            SentMessages.AddRange(messages);
            LastDelay = messageEnqueueDelay;
            return Task.CompletedTask;
        }

        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
        {
            SentMessages.Add(message);
            return Task.FromResult(0L);
        }

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
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
        public long? QueueTimeMs { get; set; }
        public long? ProcessingTimeMs { get; set; }

        public Task Complete(CancellationToken ct = default) => Task.CompletedTask;
        public Task Abandon(TransientException ex) => Task.CompletedTask;
        public Task DeadLetter(string reason, Exception ex = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task Defer(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeferOnly(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IMessageContext> ReceiveNextDeferred(CancellationToken ct = default) => Task.FromResult<IMessageContext>(null);
        public Task<IMessageContext> ReceiveNextDeferredWithPop(CancellationToken ct = default) => Task.FromResult<IMessageContext>(null);
        public Task BlockSession(CancellationToken ct = default) => Task.CompletedTask;
        public Task UnblockSession(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> IsSessionBlocked(CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> IsSessionBlockedByThis(CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> IsSessionBlockedByEventId(CancellationToken ct = default) => Task.FromResult(false);
        public Task<string> GetBlockedByEventId(CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task<int> GetNextDeferralSequenceAndIncrement(CancellationToken ct = default) => Task.FromResult(0);
        public Task IncrementDeferredCount(CancellationToken ct = default) => Task.CompletedTask;
        public Task DecrementDeferredCount(CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> GetDeferredCount(CancellationToken ct = default) => Task.FromResult(0);
        public Task<bool> HasDeferredMessages(CancellationToken ct = default) => Task.FromResult(false);
        public Task ResetDeferredCount(CancellationToken ct = default) => Task.CompletedTask;
        public Task ScheduleRedelivery(TimeSpan delay, int throttleRetryCount, CancellationToken ct = default) => Task.CompletedTask;
    }
}
