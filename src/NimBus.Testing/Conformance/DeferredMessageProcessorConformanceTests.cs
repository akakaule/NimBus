using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Deferral;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;

namespace NimBus.Testing.Conformance;

/// <summary>
/// Provider-agnostic conformance suite for the park-and-replay primitive
/// (<see cref="IParkedMessageStore"/> + <see cref="ISessionStateStore"/>
/// checkpoint methods + <see cref="PortableDeferredMessageProcessor"/>). Each
/// concrete provider supplies a freshly-initialised parked-store, session-
/// state store, and message-tracking store; the abstract suite exercises:
/// <list type="bullet">
///   <item>Park: stores the message, increments the active-park counter, emits
///         exactly one Parked audit row.</item>
///   <item>Park (duplicate MessageId): no-op, does NOT double-audit, does NOT
///         double-bump the counter.</item>
///   <item>Replay: drains all active rows in <c>ParkSequence ASC</c>, sends
///         each through <see cref="ISender"/>, marks each replayed, advances
///         the checkpoint, decrements the counter to zero, emits the audit
///         trail (ReplayStarted + per-row Replayed + ReplayCompleted).</item>
///   <item>Replay caught race: a park that lands during replay is picked up
///         before the loop declares the session drained (design §6.3).</item>
///   <item>Crash mid-replay: the active-only filter on
///         <see cref="IParkedMessageStore.GetActiveAsync"/> means a row
///         already MarkReplayed-ed is filtered out on restart; the resume
///         processes the next un-replayed row.</item>
///   <item>Skip-by-operator: marks rows skipped, decrements the counter,
///         emits the per-row ReplaySkippedByOperator audit with the operator
///         id.</item>
/// </list>
///
/// See <c>docs/specs/003-rabbitmq-transport/deferred-by-session-design.md</c>
/// §12 for the conformance/test plan.
/// </summary>
[TestClass]
public abstract class DeferredMessageProcessorConformanceTests
{
    private readonly string _scope = $"dpc-{Guid.NewGuid():N}"[..16];

    /// <summary>Override to supply a freshly-initialised parked-message store.</summary>
    protected abstract IParkedMessageStore CreateParkedStore();

    /// <summary>
    /// Override to supply the same provider's session-state store. The InMemory
    /// provider's parked store needs the *same* instance the session-state
    /// store hands out so the active-park counter stays in sync; SQL/Cosmos
    /// can return a fresh store each time because they share the underlying
    /// rows.
    /// </summary>
    protected abstract ISessionStateStore CreateSessionStateStore();

    /// <summary>
    /// Override to supply the message-tracking store the audit emitter writes
    /// into. The conformance suite reads back via <c>GetMessageAudits</c> to
    /// assert the audit trail.
    /// </summary>
    protected abstract IMessageTrackingStore CreateTrackingStore();

    protected string Id(string value) => $"{_scope}-{value}";

    private static IReceivedMessage SampleInbound(string endpointId, string sessionId, string eventId, string messageId, string eventTypeId = "OrderPlaced")
        => new InboundFake
        {
            EventId = eventId,
            MessageId = messageId,
            SessionId = sessionId,
            CorrelationId = "corr-1",
            From = "publisher",
            To = endpointId,
            OriginatingFrom = "publisher",
            OriginatingMessageId = messageId,
            ParentMessageId = "self",
            MessageType = MessageType.EventRequest,
            EventTypeId = eventTypeId,
            EnqueuedTimeUtc = DateTime.UtcNow.AddSeconds(-1),
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = eventTypeId,
                    EventJson = "{}",
                },
            },
        };

    [TestMethod]
    public async Task Park_StoresAndAudits()
    {
        var endpointId = Id("ep");
        var session = "s1";
        var parked = CreateParkedStore();
        var sessionState = CreateSessionStateStore();
        var tracking = CreateTrackingStore();
        var sender = new RecordingSender();
        var emitter = new DefaultPortableDeferredAuditEmitter(tracking);
        var processor = new PortableDeferredMessageProcessor(sender, parked, sessionState, emitter);

        var inbound = SampleInbound(endpointId, session, eventId: Id("e1"), messageId: Id("m1"));
        var sequence = await processor.ParkAsync(endpointId, session, blockingEventId: Id("blocker"), inbound);

        Assert.IsTrue(sequence >= 0);

        var active = await parked.CountActiveAsync(endpointId, session);
        Assert.AreEqual(1, active);

        var counter = await sessionState.GetActiveParkCount(endpointId, session);
        Assert.AreEqual(1, counter);

        var audits = (await tracking.GetMessageAudits(Id("e1"))).ToList();
        Assert.AreEqual(1, audits.Count);
        Assert.AreEqual(MessageAuditType.Parked, audits[0].AuditType);
        Assert.AreEqual("NimBus", audits[0].AuditorName);
        Assert.IsTrue(audits[0].Comment!.Contains("Parked at endpoint"), audits[0].Comment);
        Assert.IsTrue(audits[0].Comment!.Contains(Id("blocker")), audits[0].Comment);
    }

    [TestMethod]
    public async Task Park_DuplicateMessageId_DoesNotDoubleAudit()
    {
        var endpointId = Id("ep");
        var session = "s1";
        var parked = CreateParkedStore();
        var sessionState = CreateSessionStateStore();
        var tracking = CreateTrackingStore();
        var sender = new RecordingSender();
        var emitter = new DefaultPortableDeferredAuditEmitter(tracking);
        var processor = new PortableDeferredMessageProcessor(sender, parked, sessionState, emitter);

        var inbound = SampleInbound(endpointId, session, eventId: Id("e1"), messageId: Id("m1"));
        var firstSeq = await processor.ParkAsync(endpointId, session, blockingEventId: Id("blocker"), inbound);
        var secondSeq = await processor.ParkAsync(endpointId, session, blockingEventId: Id("blocker"), inbound);

        // Idempotent on (EndpointId, MessageId): same sequence both times.
        Assert.AreEqual(firstSeq, secondSeq);

        var active = await parked.CountActiveAsync(endpointId, session);
        Assert.AreEqual(1, active);

        var counter = await sessionState.GetActiveParkCount(endpointId, session);
        Assert.AreEqual(1, counter);

        var audits = (await tracking.GetMessageAudits(Id("e1"))).ToList();
        Assert.AreEqual(1, audits.Count, "Duplicate park MUST NOT emit a second Parked audit");
    }

    [TestMethod]
    public async Task Replay_SendsInFifoOrderAndAuditsAndDrains()
    {
        var endpointId = Id("ep");
        var session = "s1";
        var parked = CreateParkedStore();
        var sessionState = CreateSessionStateStore();
        var tracking = CreateTrackingStore();
        var sender = new RecordingSender();
        var emitter = new DefaultPortableDeferredAuditEmitter(tracking);
        var processor = new PortableDeferredMessageProcessor(sender, parked, sessionState, emitter);

        // Park three messages in order m1, m2, m3.
        for (int i = 1; i <= 3; i++)
        {
            await processor.ParkAsync(endpointId, session, blockingEventId: Id("blocker"),
                SampleInbound(endpointId, session, eventId: Id($"e{i}"), messageId: Id($"m{i}")));
        }

        Assert.AreEqual(3, await parked.CountActiveAsync(endpointId, session));

        await processor.ProcessDeferredMessagesAsync(session, endpointId);

        Assert.AreEqual(0, await parked.CountActiveAsync(endpointId, session));
        Assert.AreEqual(0, await sessionState.GetActiveParkCount(endpointId, session));

        // FIFO: messages were sent in m1, m2, m3 order.
        Assert.AreEqual(3, sender.SentMessages.Count);
        Assert.AreEqual(Id("m1"), sender.SentMessages[0].MessageId);
        Assert.AreEqual(Id("m2"), sender.SentMessages[1].MessageId);
        Assert.AreEqual(Id("m3"), sender.SentMessages[2].MessageId);

        // ReplayStarted audit recorded against the BLOCKING event id.
        var blockerAudits = (await tracking.GetMessageAudits(Id("blocker"))).ToList();
        Assert.IsTrue(blockerAudits.Any(a => a.AuditType == MessageAuditType.ReplayStarted));
        Assert.IsTrue(blockerAudits.Any(a => a.AuditType == MessageAuditType.ReplayCompleted));

        // Each parked event id has Parked + Replayed audits.
        for (int i = 1; i <= 3; i++)
        {
            var perRow = (await tracking.GetMessageAudits(Id($"e{i}"))).ToList();
            Assert.IsTrue(perRow.Any(a => a.AuditType == MessageAuditType.Parked), $"missing Parked for e{i}");
            Assert.IsTrue(perRow.Any(a => a.AuditType == MessageAuditType.Replayed), $"missing Replayed for e{i}");
        }
    }

    [TestMethod]
    public async Task Replay_LateParkBeforeDrainedCheck_IsCaughtUp()
    {
        var endpointId = Id("ep");
        var session = "s1";
        var parked = CreateParkedStore();
        var sessionState = CreateSessionStateStore();
        var tracking = CreateTrackingStore();
        var emitter = new DefaultPortableDeferredAuditEmitter(tracking);

        // Park m1 first, then on Send(m1) park m2 — simulates the race window
        // where a parker observes the unblocked state late and parks while
        // replay is in progress (design §6.3).
        var sender = new InterceptingSender();
        var processor = new PortableDeferredMessageProcessor(sender, parked, sessionState, emitter);

        await processor.ParkAsync(endpointId, session, blockingEventId: Id("blocker"),
            SampleInbound(endpointId, session, eventId: Id("e1"), messageId: Id("m1")));

        // Wire the interceptor: when m1 is sent, park m2 mid-replay.
        sender.OnSend = async msg =>
        {
            if (msg.MessageId == Id("m1") && !sender.LateParked)
            {
                sender.LateParked = true;
                await processor.ParkAsync(endpointId, session, blockingEventId: Id("blocker"),
                    SampleInbound(endpointId, session, eventId: Id("e2"), messageId: Id("m2")));
            }
        };

        await processor.ProcessDeferredMessagesAsync(session, endpointId);

        // Both should have been replayed, even though m2 was parked mid-replay.
        Assert.AreEqual(0, await parked.CountActiveAsync(endpointId, session),
            "Late-arriving park must be drained before ProcessDeferred declares the session empty");
        Assert.AreEqual(2, sender.Recorded.Count);
        Assert.AreEqual(Id("m1"), sender.Recorded[0].MessageId);
        Assert.AreEqual(Id("m2"), sender.Recorded[1].MessageId);
    }

    [TestMethod]
    public async Task Replay_AfterPartialProgress_ResumesIdempotently()
    {
        // Simulates a crash after MarkReplayed but before the next iteration:
        // run #1 marks one row replayed but the rest stay active; run #2 picks
        // up the remaining rows without re-replaying the first.
        var endpointId = Id("ep");
        var session = "s1";
        var parked = CreateParkedStore();
        var sessionState = CreateSessionStateStore();
        var tracking = CreateTrackingStore();
        var emitter = new DefaultPortableDeferredAuditEmitter(tracking);

        // Park three.
        var park = new PortableDeferredMessageProcessor(new RecordingSender(), parked, sessionState, emitter);
        for (int i = 1; i <= 3; i++)
        {
            await park.ParkAsync(endpointId, session, blockingEventId: Id("blocker"),
                SampleInbound(endpointId, session, eventId: Id($"e{i}"), messageId: Id($"m{i}")));
        }

        // Run #1: send fails on m2 — m1 has been MarkReplayed, but the loop
        // exits before m2 is replayed.
        var sender1 = new RecordingSender();
        sender1.OnSend = msg =>
        {
            if (msg.MessageId == Id("m2"))
                throw new InvalidOperationException("transient send failure");
            return Task.CompletedTask;
        };
        var processor1 = new PortableDeferredMessageProcessor(sender1, parked, sessionState, emitter);
        try
        {
            await processor1.ProcessDeferredMessagesAsync(session, endpointId);
        }
        catch (InvalidOperationException) { /* expected — sender threw */ }

        // m1 is replayed (active count went 3 -> 2); m2 + m3 are still active.
        Assert.AreEqual(2, await parked.CountActiveAsync(endpointId, session));
        Assert.AreEqual(1, sender1.Recorded.Count);
        Assert.AreEqual(Id("m1"), sender1.Recorded[0].MessageId);

        // Run #2: send works. Resume from the checkpoint; m1 is NOT re-sent.
        var sender2 = new RecordingSender();
        var processor2 = new PortableDeferredMessageProcessor(sender2, parked, sessionState, emitter);
        await processor2.ProcessDeferredMessagesAsync(session, endpointId);

        Assert.AreEqual(0, await parked.CountActiveAsync(endpointId, session));
        Assert.AreEqual(2, sender2.Recorded.Count);
        Assert.AreEqual(Id("m2"), sender2.Recorded[0].MessageId);
        Assert.AreEqual(Id("m3"), sender2.Recorded[1].MessageId);
    }

    [TestMethod]
    public async Task SkipParked_MarksRowsAndAuditsWithOperatorId()
    {
        var endpointId = Id("ep");
        var session = "s1";
        var parked = CreateParkedStore();
        var sessionState = CreateSessionStateStore();
        var tracking = CreateTrackingStore();
        var sender = new RecordingSender();
        var emitter = new DefaultPortableDeferredAuditEmitter(tracking);
        var processor = new PortableDeferredMessageProcessor(sender, parked, sessionState, emitter);

        for (int i = 1; i <= 2; i++)
        {
            await processor.ParkAsync(endpointId, session, blockingEventId: Id("blocker"),
                SampleInbound(endpointId, session, eventId: Id($"e{i}"), messageId: Id($"m{i}")));
        }

        var rows = await parked.GetActiveAsync(endpointId, session, afterSequence: -1, limit: 100);
        Assert.AreEqual(2, rows.Count);

        await processor.SkipParkedAsync(endpointId, session, rows, operatorId: "alice@example.com", comment: "out of date");

        Assert.AreEqual(0, await parked.CountActiveAsync(endpointId, session));
        Assert.AreEqual(0, await sessionState.GetActiveParkCount(endpointId, session));

        var e1Audits = (await tracking.GetMessageAudits(Id("e1"))).ToList();
        var skipAudit = e1Audits.SingleOrDefault(a => a.AuditType == MessageAuditType.ReplaySkippedByOperator);
        Assert.IsNotNull(skipAudit);
        Assert.AreEqual("alice@example.com", skipAudit.AuditorName);
        Assert.AreEqual("out of date", skipAudit.Comment);
    }

    [TestMethod]
    public async Task GetActiveAsync_OrdersByParkSequenceAscending()
    {
        var endpointId = Id("ep");
        var session = "s1";
        var parked = CreateParkedStore();
        var sessionState = CreateSessionStateStore();
        var tracking = CreateTrackingStore();
        var sender = new RecordingSender();
        var emitter = new DefaultPortableDeferredAuditEmitter(tracking);
        var processor = new PortableDeferredMessageProcessor(sender, parked, sessionState, emitter);

        for (int i = 1; i <= 4; i++)
        {
            await processor.ParkAsync(endpointId, session, blockingEventId: Id("blocker"),
                SampleInbound(endpointId, session, eventId: Id($"e{i}"), messageId: Id($"m{i}")));
        }

        var rows = await parked.GetActiveAsync(endpointId, session, afterSequence: -1, limit: 100);
        Assert.AreEqual(4, rows.Count);
        for (int i = 1; i < rows.Count; i++)
        {
            Assert.IsTrue(rows[i].ParkSequence > rows[i - 1].ParkSequence,
                $"sequences not ascending: {rows[i - 1].ParkSequence} >= {rows[i].ParkSequence}");
        }
    }

    [TestMethod]
    public async Task ReplayCheckpoint_ForwardOnly_RejectsBackwardsAdvance()
    {
        var endpointId = Id("ep");
        var session = "s1";
        var sessionState = CreateSessionStateStore();

        // Initial state: -1 sentinel.
        Assert.AreEqual(-1, await sessionState.GetLastReplayedSequence(endpointId, session));

        // Advance from -1 -> 5: succeeds.
        Assert.IsTrue(await sessionState.TryAdvanceLastReplayedSequence(endpointId, session, expectedCurrent: -1, newValue: 5));
        Assert.AreEqual(5, await sessionState.GetLastReplayedSequence(endpointId, session));

        // Backward attempt: 5 -> 4 with expectedCurrent=5 must reject.
        Assert.IsFalse(await sessionState.TryAdvanceLastReplayedSequence(endpointId, session, expectedCurrent: 5, newValue: 4));
        Assert.AreEqual(5, await sessionState.GetLastReplayedSequence(endpointId, session));

        // Wrong expected (someone else advanced): with newValue forward but
        // wrong expected, must reject.
        Assert.IsFalse(await sessionState.TryAdvanceLastReplayedSequence(endpointId, session, expectedCurrent: 4, newValue: 6));
        Assert.AreEqual(5, await sessionState.GetLastReplayedSequence(endpointId, session));

        // Correct expected, forward: succeeds.
        Assert.IsTrue(await sessionState.TryAdvanceLastReplayedSequence(endpointId, session, expectedCurrent: 5, newValue: 6));
        Assert.AreEqual(6, await sessionState.GetLastReplayedSequence(endpointId, session));
    }

    private sealed class InboundFake : IReceivedMessage
    {
        public string EventId { get; set; } = string.Empty;
        public string MessageId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string CorrelationId { get; set; } = string.Empty;
        public string From { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
        public string OriginatingFrom { get; set; } = string.Empty;
        public string OriginatingMessageId { get; set; } = string.Empty;
        public string ParentMessageId { get; set; } = string.Empty;
        public MessageType MessageType { get; set; }
        public MessageContent MessageContent { get; set; } = new();
        public string EventTypeId { get; set; } = string.Empty;
        public string OriginalSessionId { get; set; } = string.Empty;
        public int? DeferralSequence { get; set; }
        public DateTime EnqueuedTimeUtc { get; set; }
        public int? RetryCount { get; set; }
        public string DeadLetterReason { get; set; } = string.Empty;
        public string DeadLetterErrorDescription { get; set; } = string.Empty;
        public long? QueueTimeMs { get; set; }
        public long? ProcessingTimeMs { get; set; }
    }

    private class RecordingSender : ISender
    {
        public List<IMessage> SentMessages { get; } = new();
        public List<IMessage> Recorded => SentMessages;
        public Func<IMessage, Task>? OnSend { get; set; }

        public async Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            if (OnSend is not null) await OnSend(message);
            SentMessages.Add(message);
        }

        public async Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            foreach (var m in messages) await Send(m, messageEnqueueDelay, cancellationToken);
        }

        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
            => Task.FromResult((long)0);

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class InterceptingSender : RecordingSender
    {
        public bool LateParked { get; set; }
    }
}
