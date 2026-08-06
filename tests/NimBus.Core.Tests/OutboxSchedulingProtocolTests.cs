#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Core.Messages;
using NimBus.Core.Outbox;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Tests;

/// <summary>
/// Spec 025: OutboxSender handle-based schedule/cancel and the pinned
/// OutboxDispatcher protocol selection (no coordinator / inactive / active).
/// </summary>
[TestClass]
public class OutboxSchedulingProtocolTests
{
    // ── OutboxSender: handle-based schedule/cancel ──────────────────────

    [TestMethod]
    public async Task ScheduleMessageWithHandle_ActiveMode_StoresScheduledRowAndReturnsProviderHandle()
    {
        var outbox = new FakeScheduledOutbox { DueTimeDispatchActive = true, NextSequence = 99L };
        var sender = new OutboxSender(outbox);
        var message = MarkedMessage("timeout-1");
        var due = DateTimeOffset.UtcNow.AddHours(1);

        var handle = await ((ISender)sender).ScheduleMessageWithHandle(message, due);

        Assert.AreEqual("timeout-1", handle.TimeoutId);
        Assert.AreEqual(99L, handle.SequenceNumber);
        Assert.AreEqual(ScheduledMessageHandleKind.SqlOutboxSequenceNumber, handle.Kind);
        var stored = outbox.ScheduledRows.Single();
        Assert.AreEqual(due.UtcDateTime, stored.ScheduledEnqueueTimeUtc);
        Assert.AreEqual("timeout-1", stored.MessageId);
    }

    [TestMethod]
    public async Task ScheduleMessageWithHandle_ProviderWithoutCapability_ThrowsWithoutStoring()
    {
        var outbox = new PlainOutbox();
        var sender = new OutboxSender(outbox);

        await Assert.ThrowsExactlyAsync<NotSupportedException>(
            () => ((ISender)sender).ScheduleMessageWithHandle(MarkedMessage("timeout-1"), DateTimeOffset.UtcNow.AddHours(1)));
        Assert.AreEqual(0, outbox.StoredCount, "Nothing may be stored before capability validation");
    }

    [TestMethod]
    public async Task ScheduleMessageWithHandle_DefaultMode_ProviderModeGateThrows_NothingCounted()
    {
        // The provider's own gate throws InvalidOperationException naming the mode;
        // OutboxSender must not have stored anything else beforehand.
        var outbox = new FakeScheduledOutbox { DueTimeDispatchActive = false, ThrowModeGate = true };
        var sender = new OutboxSender(outbox);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => ((ISender)sender).ScheduleMessageWithHandle(MarkedMessage("timeout-1"), DateTimeOffset.UtcNow.AddHours(1)));
        Assert.AreEqual(0, outbox.ScheduledRows.Count);
        Assert.AreEqual(0, outbox.PlainStores);
    }

    [TestMethod]
    public async Task CancelScheduledMessage_Handle_DelegatesToProviderCas()
    {
        var outbox = new FakeScheduledOutbox
        {
            DueTimeDispatchActive = true,
            CancelOutcome = ScheduledMessageCancellationOutcome.CancelledBeforeDispatch,
        };
        var sender = new OutboxSender(outbox);
        var handle = new ScheduledMessageHandle("timeout-1", 99L, ScheduledMessageHandleKind.SqlOutboxSequenceNumber);

        var outcome = await ((ISender)sender).CancelScheduledMessage(handle);

        Assert.AreEqual(ScheduledMessageCancellationOutcome.CancelledBeforeDispatch, outcome);
        Assert.AreEqual(handle, outbox.CancelledHandles.Single());
    }

    [TestMethod]
    public async Task CancelScheduledMessage_BrokerKindHandle_IsRejectedNotReinterpreted()
    {
        var outbox = new FakeScheduledOutbox { DueTimeDispatchActive = true };
        var sender = new OutboxSender(outbox);
        var handle = new ScheduledMessageHandle("timeout-1", 99L, ScheduledMessageHandleKind.BrokerSequenceNumber);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => ((ISender)sender).CancelScheduledMessage(handle));
        Assert.AreEqual(0, outbox.CancelledHandles.Count);
    }

    [TestMethod]
    public async Task LegacySchedule_ActiveMode_ReturnsProviderLocalSequence()
    {
        var outbox = new FakeScheduledOutbox { DueTimeDispatchActive = true, NextSequence = 7L };
        var sender = new OutboxSender(outbox);

        var sequence = await ((ISender)sender).ScheduleMessage(
            MarkedMessage("timeout-1"), DateTimeOffset.UtcNow.AddHours(1));

        Assert.AreEqual(7L, sequence);
        Assert.AreEqual(1, outbox.ScheduledRows.Count);
    }

    [TestMethod]
    public async Task LegacySchedule_DefaultMode_StillReturnsZero()
    {
        var outbox = new FakeScheduledOutbox { DueTimeDispatchActive = false };
        var sender = new OutboxSender(outbox);

        var sequence = await ((ISender)sender).ScheduleMessage(
            MarkedMessage("timeout-1"), DateTimeOffset.UtcNow.AddHours(1));

        Assert.AreEqual(0L, sequence);
        Assert.AreEqual(1, outbox.PlainStores, "Default mode stores through the legacy IOutbox path");
        Assert.AreEqual(0, outbox.ScheduledRows.Count);
    }

    [TestMethod]
    public async Task LegacyCancelScheduled_Long_ThrowsNotSupportedInAllModes()
    {
        var active = new OutboxSender(new FakeScheduledOutbox { DueTimeDispatchActive = true });
        var inactive = new OutboxSender(new FakeScheduledOutbox { DueTimeDispatchActive = false });

        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => ((ISender)active).CancelScheduledMessage(42L));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => ((ISender)inactive).CancelScheduledMessage(42L));
    }

    // ── OutboxDispatcher: pinned protocol selection ─────────────────────

    [TestMethod]
    public async Task Dispatcher_NoCoordinator_UsesLegacyGetPendingFlow()
    {
        var outbox = new CountingOutbox();
        outbox.AddPending(PendingRow("row-1"));
        var sender = new CountingSender();
        var dispatcher = new OutboxDispatcher(outbox, sender);

        var dispatched = await dispatcher.DispatchPendingAsync();

        Assert.AreEqual(1, dispatched);
        Assert.AreEqual(1, outbox.GetPendingCalls);
        Assert.AreEqual(1, sender.SentCount);
        CollectionAssert.AreEqual(new[] { "row-1" }, outbox.DispatchedIds.ToArray());
    }

    [TestMethod]
    public async Task Dispatcher_InactiveCoordinator_UsesLegacyFlowAndNeverCallsClaimApi()
    {
        var outbox = new CountingOutbox();
        outbox.AddPending(PendingRow("row-1"));
        var sender = new CountingSender();
        var coordinator = new FakeCoordinator { DueTimeDispatchActive = false };
        var dispatcher = new OutboxDispatcher(outbox, sender, coordinator);

        var dispatched = await dispatcher.DispatchPendingAsync();

        Assert.AreEqual(1, dispatched);
        Assert.AreEqual(1, outbox.GetPendingCalls);
        Assert.AreEqual(0, coordinator.ClaimCalls, "The claim API must never be called in default mode");
        Assert.AreEqual(0, coordinator.StartCalls);
    }

    [TestMethod]
    public async Task Dispatcher_ActiveCoordinator_RunsClaimProtocolAndNeverCallsGetPending()
    {
        var outbox = new CountingOutbox();
        var sender = new CountingSender();
        var coordinator = new FakeCoordinator { DueTimeDispatchActive = true };
        coordinator.ClaimableRows.Add(PendingRow("row-1"));
        var dispatcher = new OutboxDispatcher(outbox, sender, coordinator);

        var dispatched = await dispatcher.DispatchPendingAsync();

        Assert.AreEqual(1, dispatched);
        Assert.AreEqual(0, outbox.GetPendingCalls, "GetPendingAsync must never be called in claim mode");
        Assert.AreEqual(1, coordinator.ClaimCalls);
        Assert.IsTrue(coordinator.StartCalls >= 1);
        CollectionAssert.AreEqual(new[] { "row-1" }, coordinator.CompletedIds.ToArray());
        Assert.AreEqual(0, outbox.DispatchedIds.Count, "Checkpointing goes through the owned TryCompleteAsync, not MarkAsDispatched");
        Assert.AreEqual(1, sender.SentCount);
    }

    [TestMethod]
    public async Task Dispatcher_ActiveCoordinator_DueScheduledRow_IsSentImmediatelyNotBrokerScheduled()
    {
        var sender = new CountingSender();
        var coordinator = new FakeCoordinator { DueTimeDispatchActive = true };
        var row = PendingRow("row-1");
        row.ScheduledEnqueueTimeUtc = DateTime.UtcNow.AddMinutes(-1); // due
        coordinator.ClaimableRows.Add(row);
        var dispatcher = new OutboxDispatcher(new CountingOutbox(), sender, coordinator);

        await dispatcher.DispatchPendingAsync();

        Assert.AreEqual(1, sender.SentCount, "Due rows use Send with zero delay");
        Assert.AreEqual(0, sender.ScheduledCount, "No eager broker schedule in SqlOwnedDueTime mode");
        Assert.AreEqual(0, sender.LastDelay);
    }

    [TestMethod]
    public async Task Dispatcher_FenceLost_SkipsSendWithoutFailure()
    {
        var sender = new CountingSender();
        var coordinator = new FakeCoordinator { DueTimeDispatchActive = true, FenceResult = null };
        coordinator.ClaimableRows.Add(PendingRow("row-1"));
        var dispatcher = new OutboxDispatcher(new CountingOutbox(), sender, coordinator);

        var dispatched = await dispatcher.DispatchPendingAsync();

        Assert.AreEqual(0, dispatched);
        Assert.AreEqual(0, sender.SentCount, "A cancelled/stale row must never be sent");
        Assert.AreEqual(0, coordinator.CompletedIds.Count);
    }

    [TestMethod]
    public async Task Dispatcher_BudgetConsumedByFence_RefencesOnceForFreshWindow()
    {
        var sender = new CountingSender();
        // A window equal to the floor guarantees the first residual is below the
        // floor (any positive fence latency), forcing exactly one renewal.
        var coordinator = new FakeCoordinator
        {
            DueTimeDispatchActive = true,
            UsableSendWindow = TimeSpan.FromSeconds(5),
        };
        coordinator.ClaimableRows.Add(PendingRow("row-1"));
        var dispatcher = new OutboxDispatcher(new CountingOutbox(), sender, coordinator);

        var dispatched = await dispatcher.DispatchPendingAsync();

        Assert.AreEqual(1, dispatched);
        Assert.AreEqual(2, coordinator.StartCalls, "The dispatcher re-fences exactly once for a fresh window");
        Assert.AreEqual(1, sender.SentCount, "Broker I/O proceeds with a positive budget after renewal");
    }

    [TestMethod]
    public async Task Dispatcher_RenewalLost_AbandonsAttemptWithoutSend()
    {
        var sender = new CountingSender();
        var coordinator = new FakeCoordinator
        {
            DueTimeDispatchActive = true,
            UsableSendWindow = TimeSpan.FromSeconds(5),
            FenceResultsThenNull = 1, // first fence succeeds, renewal returns null
        };
        coordinator.ClaimableRows.Add(PendingRow("row-1"));
        var dispatcher = new OutboxDispatcher(new CountingOutbox(), sender, coordinator);

        var dispatched = await dispatcher.DispatchPendingAsync();

        Assert.AreEqual(0, dispatched);
        Assert.AreEqual(0, sender.SentCount, "Ownership lost to an expired-head reclaim: no broker I/O");
    }

    [TestMethod]
    public async Task Dispatcher_StaleCheckpoint_DoesNotCountAsDispatched()
    {
        var sender = new CountingSender();
        var coordinator = new FakeCoordinator { DueTimeDispatchActive = true, CompleteResult = false };
        coordinator.ClaimableRows.Add(PendingRow("row-1"));
        var dispatcher = new OutboxDispatcher(new CountingOutbox(), sender, coordinator);

        var dispatched = await dispatcher.DispatchPendingAsync();

        Assert.AreEqual(0, dispatched, "A stale owner's checkpoint affects zero rows");
        Assert.AreEqual(1, sender.SentCount);
    }

    [TestMethod]
    public async Task Dispatcher_SendFails_RowIsNotCheckpointed()
    {
        var sender = new CountingSender { ThrowOnSend = new InvalidOperationException("broker down") };
        var coordinator = new FakeCoordinator { DueTimeDispatchActive = true };
        coordinator.ClaimableRows.Add(PendingRow("row-1"));
        var dispatcher = new OutboxDispatcher(new CountingOutbox(), sender, coordinator);

        var dispatched = await dispatcher.DispatchPendingAsync();

        Assert.AreEqual(0, dispatched);
        Assert.AreEqual(0, coordinator.CompletedIds.Count);
        Assert.AreEqual(0, coordinator.ReleasedIds.Count,
            "A started row retains DispatchStartedAtUtc; the claim expires on its own");
    }

    [TestMethod]
    public async Task Dispatcher_ShutdownBeforeCurrentRowStarts_ReleasesTheCurrentClaimToo()
    {
        // The interrupted row is claimed but NOT dispatch-started, so leaving it
        // reserved would block its whole session until SendLeaseDuration expires
        // (configurable up to 24h). Cleanup must include the current item.
        using var cts = new CancellationTokenSource();
        var sender = new CountingSender();
        var coordinator = new FakeCoordinator { DueTimeDispatchActive = true };
        coordinator.ClaimableRows.Add(PendingRow("row-1"));
        coordinator.ClaimableRows.Add(PendingRow("row-2"));
        coordinator.BeforeStart = () => cts.Cancel();
        var dispatcher = new OutboxDispatcher(new CountingOutbox(), sender, coordinator);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => dispatcher.DispatchPendingAsync(batchSize: 10, cts.Token));

        Assert.AreEqual(0, sender.SentCount, "Shutdown landed before any send");
        CollectionAssert.AreEquivalent(
            new[] { "row-1", "row-2" },
            coordinator.ReleasedIds,
            "Both the interrupted row and the untouched remainder are released for immediate reclaim");
    }

    // ── ISender default bridge: handle validity (spec 025 invariant) ─────

    [TestMethod]
    public async Task ScheduleMessageWithHandle_DefaultBridge_PositiveSequence_ReturnsValidatedHandle()
    {
        ISender sender = new CountingSender { NextSequence = 12L };

        var handle = await sender.ScheduleMessageWithHandle(MarkedMessage("timeout-1"), DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.AreEqual("timeout-1", handle.TimeoutId);
        Assert.AreEqual(12L, handle.SequenceNumber);
        handle.Validate(nameof(handle)); // the returned handle is cancellable as-is
    }

    [TestMethod]
    public async Task ScheduleMessageWithHandle_DefaultBridge_NonPositiveSequence_IsRejectedAtScheduleTime()
    {
        // A sender that cannot produce a broker sequence (legacy/custom/test double)
        // must not yield a handle that CancelScheduled would immediately reject.
        ISender sender = new CountingSender { NextSequence = 0L };

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => sender.ScheduleMessageWithHandle(MarkedMessage("timeout-1"), DateTimeOffset.UtcNow.AddMinutes(5)));
        StringAssert.Contains(ex.Message, "non-positive sequence number");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static Message MarkedMessage(string timeoutId) => new()
    {
        To = "Test",
        MessageId = timeoutId,
        ScheduledMessageId = timeoutId,
        SessionId = "workflow-1",
        CorrelationId = "conversation-1",
        MessageType = MessageType.EventRequest,
        MessageContent = new MessageContent { EventContent = new EventContent { EventTypeId = "PaymentTimedOut", EventJson = "{}" } },
    };

    private static OutboxMessage PendingRow(string id) => new()
    {
        Id = id,
        MessageId = "msg-" + id,
        To = "Test",
        Payload = JsonConvert.SerializeObject(new Message
        {
            MessageId = "msg-" + id,
            To = "Test",
            MessageContent = new MessageContent { EventContent = new EventContent { EventTypeId = "OrderPlaced" } },
        }),
        CreatedAtUtc = DateTime.UtcNow,
    };

    private sealed class FakeScheduledOutbox : IOutbox, IScheduledOutbox, IOutboxDispatchCoordinator
    {
        public bool DueTimeDispatchActive { get; set; }
        public bool ThrowModeGate { get; set; }
        public long NextSequence { get; set; } = 1L;
        public ScheduledMessageCancellationOutcome CancelOutcome { get; set; } = ScheduledMessageCancellationOutcome.CancelledBeforeDispatch;
        public List<OutboxMessage> ScheduledRows { get; } = new();
        public List<ScheduledMessageHandle> CancelledHandles { get; } = new();
        public int PlainStores { get; private set; }

        public TimeSpan UsableSendWindow => TimeSpan.FromSeconds(25);

        public Task<long> StoreScheduledAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            if (ThrowModeGate || !DueTimeDispatchActive)
                throw new InvalidOperationException("Requires ScheduledDelivery = SqlOwnedDueTime.");
            ScheduledRows.Add(message);
            return Task.FromResult(NextSequence);
        }

        public Task<ScheduledMessageCancellationOutcome> CancelScheduledAsync(ScheduledMessageHandle handle, CancellationToken cancellationToken = default)
        {
            CancelledHandles.Add(handle);
            return Task.FromResult(CancelOutcome);
        }

        public Task StoreAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            PlainStores++;
            return Task.CompletedTask;
        }

        public Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken = default)
        {
            PlainStores += messages.Count();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OutboxMessage>>(Array.Empty<OutboxMessage>());

        public Task MarkAsDispatchedAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkAsDispatchedAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<OutboxMessage>> ClaimDueAsync(Guid claimId, int batchSize, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OutboxMessage>>(Array.Empty<OutboxMessage>());
        public Task<DateTime?> TryStartDispatchAsync(string outboxMessageId, Guid claimId, CancellationToken cancellationToken = default) =>
            Task.FromResult<DateTime?>(null);
        public Task<bool> TryCompleteAsync(string outboxMessageId, Guid claimId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
        public Task ReleaseClaimAsync(string outboxMessageId, Guid claimId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class PlainOutbox : IOutbox
    {
        public int StoredCount { get; private set; }

        public Task StoreAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            StoredCount++;
            return Task.CompletedTask;
        }

        public Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken = default)
        {
            StoredCount += messages.Count();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OutboxMessage>>(Array.Empty<OutboxMessage>());
        public Task MarkAsDispatchedAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkAsDispatchedAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CountingOutbox : IOutbox
    {
        private readonly List<OutboxMessage> _pending = new();
        public int GetPendingCalls { get; private set; }
        public List<string> DispatchedIds { get; } = new();

        public void AddPending(OutboxMessage message) => _pending.Add(message);

        public Task StoreAsync(OutboxMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default)
        {
            GetPendingCalls++;
            return Task.FromResult<IReadOnlyList<OutboxMessage>>(_pending.Take(batchSize).ToList());
        }

        public Task MarkAsDispatchedAsync(string id, CancellationToken cancellationToken = default)
        {
            DispatchedIds.Add(id);
            return Task.CompletedTask;
        }

        public Task MarkAsDispatchedAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
        {
            DispatchedIds.AddRange(ids);
            return Task.CompletedTask;
        }
    }

    private sealed class CountingSender : ISender
    {
        public int SentCount { get; private set; }
        public int ScheduledCount { get; private set; }
        public int LastDelay { get; private set; }
        public Exception ThrowOnSend { get; set; }

        public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            if (ThrowOnSend != null) throw ThrowOnSend;
            SentCount++;
            LastDelay = messageEnqueueDelay;
            return Task.CompletedTask;
        }

        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            if (ThrowOnSend != null) throw ThrowOnSend;
            SentCount += messages.Count();
            return Task.CompletedTask;
        }

        /// <summary>Sequence returned by the legacy ScheduleMessage bridge.</summary>
        public long NextSequence { get; set; } = 1L;

        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
        {
            ScheduledCount++;
            return Task.FromResult(NextSequence);
        }

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeCoordinator : IOutboxDispatchCoordinator
    {
        public bool DueTimeDispatchActive { get; set; }
        public TimeSpan UsableSendWindow { get; set; } = TimeSpan.FromSeconds(25);
        public List<OutboxMessage> ClaimableRows { get; } = new();
        public int ClaimCalls { get; private set; }
        public int StartCalls { get; private set; }
        public List<string> CompletedIds { get; } = new();
        public List<string> ReleasedIds { get; } = new();

        /// <summary>Explicit fence result; DateTime.MaxValue means "return a fresh deadline".</summary>
        public DateTime? FenceResult { get; set; } = DateTime.MaxValue;

        /// <summary>When set, the first N fence calls succeed and later ones return null.</summary>
        public int? FenceResultsThenNull { get; set; }

        public bool CompleteResult { get; set; } = true;

        public Task<IReadOnlyList<OutboxMessage>> ClaimDueAsync(Guid claimId, int batchSize, CancellationToken cancellationToken = default)
        {
            ClaimCalls++;
            var batch = ClaimableRows.Take(batchSize).ToList();
            ClaimableRows.Clear();
            return Task.FromResult<IReadOnlyList<OutboxMessage>>(batch);
        }

        /// <summary>
        /// Invoked at the top of the fence call, before it can win. Lets a test
        /// land a shutdown exactly in the window where the current row is claimed
        /// but not yet dispatch-started.
        /// </summary>
        public Action BeforeStart { get; set; }

        public Task<DateTime?> TryStartDispatchAsync(string outboxMessageId, Guid claimId, CancellationToken cancellationToken = default)
        {
            BeforeStart?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            StartCalls++;
            if (FenceResultsThenNull.HasValue)
            {
                return Task.FromResult<DateTime?>(
                    StartCalls <= FenceResultsThenNull.Value ? DateTime.UtcNow.AddSeconds(30) : null);
            }

            return Task.FromResult<DateTime?>(
                FenceResult == DateTime.MaxValue ? DateTime.UtcNow.AddSeconds(30) : FenceResult);
        }

        public Task<bool> TryCompleteAsync(string outboxMessageId, Guid claimId, CancellationToken cancellationToken = default)
        {
            if (CompleteResult)
                CompletedIds.Add(outboxMessageId);
            return Task.FromResult(CompleteResult);
        }

        public Task ReleaseClaimAsync(string outboxMessageId, Guid claimId, CancellationToken cancellationToken = default)
        {
            ReleasedIds.Add(outboxMessageId);
            return Task.CompletedTask;
        }
    }
}
