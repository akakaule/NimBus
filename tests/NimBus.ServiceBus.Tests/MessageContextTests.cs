#pragma warning disable CA1707, CA1515, CA2007
using Azure.Messaging.ServiceBus;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.ServiceBus;
using Newtonsoft.Json;
using System.Text;

namespace NimBus.ServiceBus.Tests;

[TestClass]
public class MessageContextTests
{
    // ── Constructor ──────────────────────────────────────────────────────

    [TestMethod]
    public void Constructor_NullMessage_ThrowsArgumentNull()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            new MessageContext(null, new FakeServiceBusSession()));
    }

    [TestMethod]
    public void Constructor_NullSession_ThrowsArgumentNull()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            new MessageContext(new FakeServiceBusMessage(), null));
    }

    [TestMethod]
    public void Constructor_ValidArgs_SetsIsDeferred()
    {
        var ctx = CreateMessageContext(isDeferred: true);
        Assert.IsTrue(ctx.IsDeferred);
    }

    [TestMethod]
    public void Constructor_DefaultIsDeferred_IsFalse()
    {
        var ctx = CreateMessageContext();
        Assert.IsFalse(ctx.IsDeferred);
    }

    // ── Property access ─────────────────────────────────────────────────

    [TestMethod]
    public void From_WhenPropertyExists_ReturnsValue()
    {
        var ctx = CreateMessageContext(from: "StorefrontEndpoint");
        Assert.AreEqual("StorefrontEndpoint", ctx.From);
    }

    [TestMethod]
    public void From_WhenPropertyMissing_ThrowsInvalidMessage()
    {
        var msg = new FakeServiceBusMessage(); // no properties set
        var session = new FakeServiceBusSession();
        var ctx = new MessageContext(msg, session);

        Assert.ThrowsException<InvalidMessageException>(() => ctx.From);
    }

    [TestMethod]
    public void OriginatingFrom_WhenMissing_ReturnsNull()
    {
        var msg = new FakeServiceBusMessage();
        var session = new FakeServiceBusSession();
        var ctx = new MessageContext(msg, session);

        Assert.IsNull(ctx.OriginatingFrom);
    }

    [TestMethod]
    public void EventTypeId_WhenMissing_ReturnsNull()
    {
        var msg = new FakeServiceBusMessage();
        var session = new FakeServiceBusSession();
        var ctx = new MessageContext(msg, session);

        Assert.IsNull(ctx.EventTypeId);
    }

    [TestMethod]
    public void MessageType_ValidValue_ParsesCorrectly()
    {
        var ctx = CreateMessageContext(messageType: MessageType.EventRequest);
        Assert.AreEqual(MessageType.EventRequest, ctx.MessageType);
    }

    [TestMethod]
    public void MessageType_InvalidValue_ThrowsInvalidMessage()
    {
        var msg = new FakeServiceBusMessage();
        msg.UserProperties[UserPropertyName.MessageType.ToString()] = "InvalidType";
        var session = new FakeServiceBusSession();
        var ctx = new MessageContext(msg, session);

        Assert.ThrowsException<InvalidMessageException>(() => ctx.MessageType);
    }

    [TestMethod]
    public void RetryCount_ValidInt_ReturnsValue()
    {
        var ctx = CreateMessageContext();
        ctx.SetUserProperty(UserPropertyName.RetryCount, "3");
        Assert.AreEqual(3, ctx.RetryCount);
    }

    [TestMethod]
    public void RetryCount_Missing_ReturnsNull()
    {
        var msg = new FakeServiceBusMessage();
        msg.UserProperties[UserPropertyName.From.ToString()] = "test";
        msg.UserProperties[UserPropertyName.MessageType.ToString()] = "EventRequest";
        var session = new FakeServiceBusSession();
        var ctx = new MessageContext(msg, session);

        Assert.IsNull(ctx.RetryCount);
    }

    [TestMethod]
    public void RetryCount_NonNumeric_ReturnsNull()
    {
        var ctx = CreateMessageContext();
        ctx.SetUserProperty(UserPropertyName.RetryCount, "abc");
        Assert.IsNull(ctx.RetryCount);
    }

    [TestMethod]
    public void DeferralSequence_ValidInt_ReturnsValue()
    {
        var ctx = CreateMessageContext();
        ctx.SetUserProperty(UserPropertyName.DeferralSequence, "5");
        Assert.AreEqual(5, ctx.DeferralSequence);
    }

    [TestMethod]
    public void DeferralSequence_Missing_ReturnsNull()
    {
        var ctx = CreateMessageContext();
        Assert.IsNull(ctx.DeferralSequence);
    }

    [TestMethod]
    public void MessageId_ReturnsValue()
    {
        var msg = new FakeServiceBusMessage { MessageId = "msg-1" };
        SetDefaultProperties(msg);
        var ctx = new MessageContext(msg, new FakeServiceBusSession());

        Assert.AreEqual("msg-1", ctx.MessageId);
    }

    [TestMethod]
    public void MessageId_Null_ThrowsInvalidMessage()
    {
        var msg = new FakeServiceBusMessage { MessageId = null };
        SetDefaultProperties(msg);
        var ctx = new MessageContext(msg, new FakeServiceBusSession());

        Assert.ThrowsException<InvalidMessageException>(() => ctx.MessageId);
    }

    [TestMethod]
    public void SessionId_ReturnsValue()
    {
        var msg = new FakeServiceBusMessage { SessionId = "session-1" };
        SetDefaultProperties(msg);
        var ctx = new MessageContext(msg, new FakeServiceBusSession());

        Assert.AreEqual("session-1", ctx.SessionId);
    }

    [TestMethod]
    public void SessionId_Null_ThrowsInvalidMessage()
    {
        var msg = new FakeServiceBusMessage { SessionId = null };
        SetDefaultProperties(msg);
        var ctx = new MessageContext(msg, new FakeServiceBusSession());

        Assert.ThrowsException<InvalidMessageException>(() => ctx.SessionId);
    }

    [TestMethod]
    public void OriginatingMessageId_WhenPropertyExists_ReturnsValue()
    {
        var ctx = CreateMessageContext();
        ctx.SetUserProperty(UserPropertyName.OriginatingMessageId, "orig-1");
        Assert.AreEqual("orig-1", ctx.OriginatingMessageId);
    }

    [TestMethod]
    public void OriginatingMessageId_WhenMissing_ReturnsSelf()
    {
        var ctx = CreateMessageContext();
        Assert.AreEqual(Constants.Self, ctx.OriginatingMessageId);
    }

    [TestMethod]
    public void ParentMessageId_WhenPropertyExists_ReturnsValue()
    {
        var ctx = CreateMessageContext();
        ctx.SetUserProperty(UserPropertyName.ParentMessageId, "parent-1");
        Assert.AreEqual("parent-1", ctx.ParentMessageId);
    }

    [TestMethod]
    public void ParentMessageId_WhenMissing_ReturnsSelf()
    {
        var ctx = CreateMessageContext();
        Assert.AreEqual(Constants.Self, ctx.ParentMessageId);
    }

    [TestMethod]
    public void MessageContent_ValidJson_Deserializes()
    {
        var content = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" },
        };
        var ctx = CreateMessageContext(content: content);

        Assert.AreEqual("OrderPlaced", ctx.MessageContent.EventContent.EventTypeId);
    }

    [TestMethod]
    public void ThrottleRetryCount_ValidValue_ReturnsCount()
    {
        var msg = new FakeServiceBusMessage();
        SetDefaultProperties(msg);
        msg.UserProperties["ThrottleRetryCount"] = "3";
        var ctx = new MessageContext(msg, new FakeServiceBusSession());

        Assert.AreEqual(3, ctx.ThrottleRetryCount);
    }

    [TestMethod]
    public void ThrottleRetryCount_Missing_ReturnsZero()
    {
        var ctx = CreateMessageContext();
        Assert.AreEqual(0, ctx.ThrottleRetryCount);
    }

    [TestMethod]
    public void ThrottleRetryCount_Invalid_ReturnsZero()
    {
        var msg = new FakeServiceBusMessage();
        SetDefaultProperties(msg);
        msg.UserProperties["ThrottleRetryCount"] = "not-a-number";
        var ctx = new MessageContext(msg, new FakeServiceBusSession());

        Assert.AreEqual(0, ctx.ThrottleRetryCount);
    }

    [TestMethod]
    public void DeadLetterReason_WhenMissing_ReturnsNull()
    {
        var ctx = CreateMessageContext();
        Assert.IsNull(ctx.DeadLetterReason);
    }

    [TestMethod]
    public void DeadLetterErrorDescription_WhenMissing_ReturnsNull()
    {
        var ctx = CreateMessageContext();
        Assert.IsNull(ctx.DeadLetterErrorDescription);
    }

    // ── Session-state bridge tests removed in #17 ──────────────────────
    // The 12 obsolete IMessageContext session-state bridges (BlockSession,
    // IsSessionBlocked*, GetBlockedByEventId, deferred-count helpers) were
    // dropped from the interface and from MessageContext in follow-up D.
    // Their behaviour is now covered by the ISessionStateStore conformance
    // suite (NimBus.MessageStore.* test runs).

    // ── Complete/DeadLetter/Defer operations ────────────────────────────

    [TestMethod]
    public async Task Complete_DelegatesToSession()
    {
        var session = new FakeServiceBusSession();
        var ctx = CreateMessageContext(session: session);

        await ctx.Complete();

        Assert.AreEqual(1, session.CompleteCalls);
    }

    [TestMethod]
    public async Task Complete_SessionLockLost_ThrowsTransient()
    {
        var session = new FakeServiceBusSession
        {
            CompleteException = new ServiceBusException("lost", ServiceBusFailureReason.SessionLockLost),
        };
        var ctx = CreateMessageContext(session: session);

        await Assert.ThrowsExceptionAsync<TransientException>(() => ctx.Complete());
    }

    [TestMethod]
    public async Task Complete_TransientServiceBusException_ThrowsTransient()
    {
        var session = new FakeServiceBusSession
        {
            CompleteException = new ServiceBusException(isTransient: true, "transient"),
        };
        var ctx = CreateMessageContext(session: session);

        await Assert.ThrowsExceptionAsync<TransientException>(() => ctx.Complete());
    }

    [TestMethod]
    public async Task DeadLetter_DelegatesToSession()
    {
        var session = new FakeServiceBusSession();
        var ctx = CreateMessageContext(session: session);

        await ctx.DeadLetter("reason", new InvalidOperationException("boom"));

        Assert.AreEqual(1, session.DeadLetterCalls);
        Assert.AreEqual("reason", session.LastDeadLetterReason);
    }

    [TestMethod]
    public async Task DeadLetter_NullException_PassesNullDescription()
    {
        var session = new FakeServiceBusSession();
        var ctx = CreateMessageContext(session: session);

        await ctx.DeadLetter("reason");

        Assert.AreEqual(1, session.DeadLetterCalls);
        Assert.IsNull(session.LastDeadLetterDescription);
    }

    [TestMethod]
    public async Task DeadLetter_LongException_TruncatesAtNewline()
    {
        var session = new FakeServiceBusSession();
        var ctx = CreateMessageContext(session: session);

        // Create an exception whose ToString() is > 4096 chars with newlines
        var longMessage = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"Line {i}: " + new string('x', 30)));
        var ex = new InvalidOperationException(longMessage);

        await ctx.DeadLetter("reason", ex);

        Assert.IsNotNull(session.LastDeadLetterDescription);
        Assert.IsTrue(session.LastDeadLetterDescription.Length <= 4096,
            $"Description should be at most 4096 chars, was {session.LastDeadLetterDescription.Length}");
        Assert.IsTrue(session.LastDeadLetterDescription.EndsWith("...[TRUNCATED]..."));
    }

    [TestMethod]
    public async Task Defer_WhenNotDeferred_DelegatesAndUpdatesState()
    {
        var session = new FakeServiceBusSession();
        var msg = CreateDefaultMessage();
        msg.SequenceNumber = 42;
        var ctx = new MessageContext(msg, session, isDeferred: false);

        await ctx.Defer();

        Assert.AreEqual(1, session.DeferCalls);
        Assert.IsTrue(session.State.DeferredSequenceNumbers.Contains(42));
    }

    [TestMethod]
    public async Task Defer_WhenAlreadyDeferred_ThrowsNotSupported()
    {
        var ctx = CreateMessageContext(isDeferred: true);

        await Assert.ThrowsExceptionAsync<NotSupportedException>(() => ctx.Defer());
    }

    [TestMethod]
    public async Task DeferOnly_WhenNotDeferred_DelegatesWithoutUpdatingState()
    {
        var session = new FakeServiceBusSession();
        var ctx = CreateMessageContext(session: session, isDeferred: false);

        await ctx.DeferOnly();

        Assert.AreEqual(1, session.DeferCalls);
        Assert.AreEqual(0, session.State.DeferredSequenceNumbers.Count, "Should not update session state");
    }

    [TestMethod]
    public async Task DeferOnly_WhenAlreadyDeferred_ThrowsNotSupported()
    {
        var ctx = CreateMessageContext(isDeferred: true);

        await Assert.ThrowsExceptionAsync<NotSupportedException>(() => ctx.DeferOnly());
    }

    [TestMethod]
    public async Task Abandon_ReturnsImmediately()
    {
        var ctx = CreateMessageContext();
        // Should not throw, just return
        await ctx.Abandon(new TransientException("test"));
    }

    // ── ReceiveNextDeferred ─────────────────────────────────────────────

    [TestMethod]
    public async Task ReceiveNextDeferred_HasSequence_ReturnsMessage()
    {
        var deferredMsg = new FakeServiceBusMessage { MessageId = "deferred-1", SessionId = "s1" };
        SetDefaultProperties(deferredMsg);
        var session = new FakeServiceBusSession();
        session.State.DeferredSequenceNumbers.Add(100);
        session.DeferredMessages[100] = deferredMsg;
        var ctx = CreateMessageContext(session: session);

        var result = await ctx.ReceiveNextDeferred();

        Assert.IsNotNull(result);
        Assert.IsTrue(result.IsDeferred);
    }

    [TestMethod]
    public async Task ReceiveNextDeferred_NullMessage_RemovesSequenceAndReturnsNull()
    {
        var session = new FakeServiceBusSession();
        session.State.DeferredSequenceNumbers.Add(100);
        // No message mapped for sequence 100 => returns null
        var ctx = CreateMessageContext(session: session);

        var result = await ctx.ReceiveNextDeferred();

        Assert.IsNull(result);
        Assert.AreEqual(0, session.State.DeferredSequenceNumbers.Count, "Should have removed the null reference");
    }

    [TestMethod]
    public async Task ReceiveNextDeferred_NoSequences_ReturnsNull()
    {
        var ctx = CreateMessageContext();
        var result = await ctx.ReceiveNextDeferred();
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task ReceiveNextDeferredWithPop_HasSequence_ReturnsMessageAndRemoves()
    {
        var deferredMsg = new FakeServiceBusMessage { MessageId = "deferred-1", SessionId = "s1" };
        SetDefaultProperties(deferredMsg);
        var session = new FakeServiceBusSession();
        session.State.DeferredSequenceNumbers.Add(100);
        session.DeferredMessages[100] = deferredMsg;
        var ctx = CreateMessageContext(session: session);

        var result = await ctx.ReceiveNextDeferredWithPop();

        Assert.IsNotNull(result);
        Assert.IsTrue(result.IsDeferred);
        Assert.AreEqual(0, session.State.DeferredSequenceNumbers.Count, "Should have removed the sequence");
    }

    // ── EnqueuedTimeUtc ─────────────────────────────────────────────────

    [TestMethod]
    public void EnqueuedTimeUtc_ReturnsValueFromMessage()
    {
        var expected = new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc);
        var msg = CreateDefaultMessage();
        msg.EnqueuedTimeUtc = expected;
        var ctx = new MessageContext(msg, new FakeServiceBusSession());

        Assert.AreEqual(expected, ctx.EnqueuedTimeUtc);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static TestableMessageContext CreateMessageContext(
        FakeServiceBusSession session = null,
        string from = "StorefrontEndpoint",
        string eventId = "evt-1",
        MessageType messageType = MessageType.EventRequest,
        MessageContent content = null,
        bool isDeferred = false)
    {
        session ??= new FakeServiceBusSession();
        var msg = CreateDefaultMessage(from, eventId, messageType, content);
        var ctx = new TestableMessageContext(msg, session, isDeferred);
        return ctx;
    }

    private static FakeServiceBusMessage CreateDefaultMessage(
        string from = "StorefrontEndpoint",
        string eventId = "evt-1",
        MessageType messageType = MessageType.EventRequest,
        MessageContent content = null)
    {
        content ??= new MessageContent
        {
            EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" },
        };
        var body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(content));
        var msg = new FakeServiceBusMessage
        {
            MessageId = "msg-1",
            SessionId = "session-1",
            CorrelationId = "corr-1",
            Body = body,
            EnqueuedTimeUtc = new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc),
        };
        SetDefaultProperties(msg, from, eventId, messageType);
        return msg;
    }

    private static void SetDefaultProperties(
        FakeServiceBusMessage msg,
        string from = "StorefrontEndpoint",
        string eventId = "evt-1",
        MessageType messageType = MessageType.EventRequest)
    {
        msg.UserProperties[UserPropertyName.From.ToString()] = from;
        msg.UserProperties[UserPropertyName.To.ToString()] = "AnalyticsEndpoint";
        msg.UserProperties[UserPropertyName.EventId.ToString()] = eventId;
        msg.UserProperties[UserPropertyName.MessageType.ToString()] = messageType.ToString();
    }

    // ── Testable subclass to expose SetUserProperty ─────────────────────

    private sealed class TestableMessageContext : MessageContext
    {
        private readonly FakeServiceBusMessage _msg;

        public TestableMessageContext(FakeServiceBusMessage msg, IServiceBusSession session, bool isDeferred = false)
            : base(msg, session, isDeferred)
        {
            _msg = msg;
        }

        public void SetUserProperty(UserPropertyName name, string value)
        {
            _msg.UserProperties[name.ToString()] = value;
        }
    }

    // ── Fakes ────────────────────────────────────────────────────────────

    private sealed class FakeServiceBusMessage : IServiceBusMessage
    {
        public Dictionary<string, string> UserProperties { get; } = new();
        public byte[] Body { get; set; } = Array.Empty<byte>();
        public string LockToken { get; set; } = "lock-1";
        public string SessionId { get; set; } = "session-1";
        public string MessageId { get; set; } = "msg-1";
        public string CorrelationId { get; set; } = "corr-1";
        public int DeliveryCount { get; set; } = 1;
        public long SequenceNumber { get; set; } = 1;
        public DateTime EnqueuedTimeUtc { get; set; } = DateTime.UtcNow;

        // IServiceBusMessage.Message is internal — only used by ScheduleRedelivery.
        // Accessible via InternalsVisibleTo; returns null since we don't unit-test ScheduleRedelivery.
        ServiceBusReceivedMessage IServiceBusMessage.Message => null;

        public string GetUserProperty(UserPropertyName name) => GetUserProperty(name.ToString());

        public string GetUserProperty(string name) =>
            UserProperties.TryGetValue(name, out var value) ? value : null;
    }

    private sealed class FakeServiceBusSession : IServiceBusSession
    {
        public SessionState State { get; set; } = new();
        public Dictionary<long, IServiceBusMessage> DeferredMessages { get; } = new();

        public int CompleteCalls { get; private set; }
        public int DeadLetterCalls { get; private set; }
        public int DeferCalls { get; private set; }
        public string LastDeadLetterReason { get; private set; }
        public string LastDeadLetterDescription { get; private set; }

        public Exception CompleteException { get; set; }
        public Exception DeadLetterException { get; set; }
        public Exception DeferException { get; set; }

        public Task CompleteAsync(IServiceBusMessage message, CancellationToken ct = default)
        {
            if (CompleteException != null) throw CompleteException;
            CompleteCalls++;
            return Task.CompletedTask;
        }

        public Task DeadLetterAsync(IServiceBusMessage message, string reason, string description, CancellationToken ct = default)
        {
            if (DeadLetterException != null) throw DeadLetterException;
            DeadLetterCalls++;
            LastDeadLetterReason = reason;
            LastDeadLetterDescription = description;
            return Task.CompletedTask;
        }

        public Task DeferAsync(IServiceBusMessage message, CancellationToken ct = default)
        {
            if (DeferException != null) throw DeferException;
            DeferCalls++;
            return Task.CompletedTask;
        }

        public Task<IServiceBusMessage> ReceiveDeferredMessageAsync(long seq, CancellationToken ct = default)
        {
            DeferredMessages.TryGetValue(seq, out var msg);
            return Task.FromResult(msg);
        }

        public Task SetStateAsync(SessionState state, CancellationToken ct = default)
        {
            State = state;
            return Task.CompletedTask;
        }

        public Task<SessionState> GetStateAsync(CancellationToken ct = default)
        {
            return Task.FromResult(State);
        }

        public Task SendScheduledMessageAsync(Azure.Messaging.ServiceBus.ServiceBusMessage message, DateTimeOffset scheduledTime, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
    }
}
