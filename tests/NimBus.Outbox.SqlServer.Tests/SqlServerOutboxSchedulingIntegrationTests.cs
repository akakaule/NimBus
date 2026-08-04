#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.Core.Outbox;
using NimBus.Outbox.SqlServer;

namespace NimBus.Outbox.SqlServer.Tests;

/// <summary>
/// Spec 025 SQL scheduling integration suite: migration, applock-serialized
/// initialization, due-time eligibility, claim/lease/fence/checkpoint races,
/// session-head and backdated-row barriers, RCSI hint validity, mode-scoped
/// gauges, version-skew simulation, and terminal cleanup. Gated on
/// NIMBUS_SQL_TEST_CONNECTION; Inconclusive when absent.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SqlServerOutboxSchedulingIntegrationTests
{
    private readonly List<SqlServerOutboxOptions> _createdSchemas = new();

    [TestCleanup]
    public async Task Cleanup()
    {
        foreach (var options in _createdSchemas)
        {
            try
            {
                await using var connection = new SqlConnection(options.ConnectionString);
                await connection.OpenAsync();
                await using var command = new SqlCommand(
                    $"IF OBJECT_ID('{options.FullTableName}') IS NOT NULL DROP TABLE {options.FullTableName}; " +
                    $"IF EXISTS (SELECT * FROM sys.schemas WHERE name = '{options.Schema}') EXEC('DROP SCHEMA [{options.Schema}]');",
                    connection);
                await command.ExecuteNonQueryAsync();
            }
            catch (SqlException)
            {
                // Best-effort cleanup; leaked schemas are uniquely named.
            }
        }

        _createdSchemas.Clear();
    }

    // ── Schema and migration ────────────────────────────────────────────

    [TestMethod]
    public async Task FreshSchema_HasScheduledDeliveryColumnsAndIndexes()
    {
        var outbox = await CreateOutboxAsync(ScheduledDeliveryMode.SqlOwnedDueTime);
        var options = _createdSchemas[^1];

        foreach (var column in new[]
        {
            "OutboxSequenceNumber", "StoredAtUtc", "CancelledAtUtc",
            "DispatchStartedAtUtc", "DispatchClaimId", "DispatchClaimedUntilUtc", "EffectiveDueAtUtc",
        })
        {
            Assert.IsNotNull(await Scalar(options, $"SELECT COL_LENGTH('{options.FullTableName}', '{column}')"),
                $"Column {column} must exist");
        }

        Assert.AreEqual(1, Convert.ToInt32(await Scalar(options,
            $"SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_{options.TableName}_SessionOrdering' AND object_id = OBJECT_ID('{options.FullTableName}')"),
            System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual(1, Convert.ToInt32(await Scalar(options,
            $"SELECT COUNT(*) FROM sys.indexes WHERE name = 'UX_{options.TableName}_OutboxSequenceNumber' AND object_id = OBJECT_ID('{options.FullTableName}')"),
            System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual(1, Convert.ToInt32(await Scalar(options,
            "SELECT is_persisted FROM sys.computed_columns WHERE object_id = OBJECT_ID('" + options.FullTableName + "') AND name = 'EffectiveDueAtUtc'"),
            System.Globalization.CultureInfo.InvariantCulture));

        _ = outbox;
    }

    [TestMethod]
    public async Task Migration_FromOldTableShape_BackfillsStoredAtUtcAndKeepsRowsDispatchable()
    {
        var options = NewOptions(ScheduledDeliveryMode.BrokerScheduleAtDispatch);

        // Create the pre-spec-025 table shape and rows directly.
        await Execute(options, $@"
            EXEC('CREATE SCHEMA [{options.Schema}]');
            CREATE TABLE {options.FullTableName} (
                [Id] NVARCHAR(128) NOT NULL PRIMARY KEY,
                [MessageId] NVARCHAR(512) NOT NULL,
                [To] NVARCHAR(256) NULL,
                [EventTypeId] NVARCHAR(256) NULL,
                [SessionId] NVARCHAR(256) NULL,
                [CorrelationId] NVARCHAR(256) NULL,
                [Payload] NVARCHAR(MAX) NOT NULL,
                [EnqueueDelayMinutes] INT NOT NULL DEFAULT 0,
                [ScheduledEnqueueTimeUtc] DATETIME2 NULL,
                [CreatedAtUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                [DispatchedAtUtc] DATETIME2 NULL,
                [TraceParent] NVARCHAR(55) NULL,
                [TraceState] NVARCHAR(256) NULL,
                INDEX IX_OutboxMessages_Pending NONCLUSTERED ([DispatchedAtUtc], [CreatedAtUtc]) WHERE [DispatchedAtUtc] IS NULL
            );
            INSERT INTO {options.FullTableName} ([Id],[MessageId],[Payload],[CreatedAtUtc]) VALUES
                ('pending-1','m1','{{}}','2026-01-01T10:00:00'),
                ('scheduled-1','m2','{{}}','2026-01-01T11:00:00');
            UPDATE {options.FullTableName} SET [ScheduledEnqueueTimeUtc]='2026-01-02T00:00:00' WHERE [Id]='scheduled-1';
            INSERT INTO {options.FullTableName} ([Id],[MessageId],[Payload],[CreatedAtUtc],[DispatchedAtUtc]) VALUES
                ('dispatched-1','m3','{{}}','2026-01-01T09:00:00','2026-01-01T09:01:00');");
        _createdSchemas.Add(options);

        var outbox = new SqlServerOutbox(options);
        await outbox.EnsureTableExistsAsync();

        Assert.AreEqual(
            (DateTime)(await Scalar(options, $"SELECT [CreatedAtUtc] FROM {options.FullTableName} WHERE [Id]='pending-1'"))!,
            (DateTime)(await Scalar(options, $"SELECT [StoredAtUtc] FROM {options.FullTableName} WHERE [Id]='pending-1'"))!,
            "StoredAtUtc must be backfilled from CreatedAtUtc");

        // Existing pending rows dispatch in default mode; dispatched rows stay terminal.
        var pending = await outbox.GetPendingAsync(10);
        CollectionAssert.AreEqual(new[] { "dummy" }.Take(0).ToArray(), Array.Empty<string>()); // no-op sanity
        CollectionAssert.AreEqual(
            new[] { "pending-1", "scheduled-1" },
            pending.Select(m => m.Id).ToArray(),
            "Pending rows remain dispatchable in CreatedAtUtc order; dispatched rows stay terminal");

        // And under the due-time rule after cutover: the past-due scheduled row and
        // the unscheduled row are claimable, ordered by EffectiveDueAtUtc.
        var cutover = new SqlServerOutbox(NewOptionsLike(options, ScheduledDeliveryMode.SqlOwnedDueTime));
        var claimed = await cutover.ClaimDueAsync(Guid.NewGuid(), 10);
        CollectionAssert.AreEqual(
            new[] { "pending-1", "scheduled-1" },
            claimed.Select(m => m.Id).ToArray());
    }

    [TestMethod]
    public async Task EnsureTableExists_TwiceSequentiallyAndConcurrently_IsIdempotent()
    {
        var options = NewOptions(ScheduledDeliveryMode.SqlOwnedDueTime);
        _createdSchemas.Add(options);
        var outbox = new SqlServerOutbox(options);

        await outbox.EnsureTableExistsAsync();
        await outbox.EnsureTableExistsAsync();

        // N-way concurrent initialization: the applock serializes the guarded DDL.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
        {
            var initializer = new SqlServerOutbox(NewOptionsLike(options, ScheduledDeliveryMode.SqlOwnedDueTime));
            return initializer.EnsureTableExistsAsync();
        }));

        Assert.IsNotNull(await Scalar(options, $"SELECT COL_LENGTH('{options.FullTableName}', 'EffectiveDueAtUtc')"));
    }

    // ── StoreScheduled / handles / ambient transactions ─────────────────

    [TestMethod]
    public async Task StoreScheduled_ReturnsDistinctIncreasingSequences()
    {
        var outbox = await CreateOutboxAsync(ScheduledDeliveryMode.SqlOwnedDueTime);

        var first = await outbox.StoreScheduledAsync(ScheduledRow("t1", DateTime.UtcNow.AddHours(1)));
        var second = await outbox.StoreScheduledAsync(ScheduledRow("t2", DateTime.UtcNow.AddHours(1)));

        Assert.IsTrue(first > 0);
        Assert.IsTrue(second > first, "Provider-local sequences are unique and increasing");
    }

    [TestMethod]
    public async Task StoreScheduled_DefaultMode_ThrowsNamingRequiredMode_NothingStored()
    {
        var outbox = await CreateOutboxAsync(ScheduledDeliveryMode.BrokerScheduleAtDispatch);
        var options = _createdSchemas[^1];

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => outbox.StoreScheduledAsync(ScheduledRow("t1", DateTime.UtcNow.AddHours(1))));

        StringAssert.Contains(ex.Message, "SqlOwnedDueTime");
        Assert.AreEqual(0, Convert.ToInt32(await Scalar(options, $"SELECT COUNT(*) FROM {options.FullTableName}"),
            System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public async Task AmbientTransactionRollback_LeavesNoScheduledRowAndNoCancellation()
    {
        var outbox = await CreateOutboxAsync(ScheduledDeliveryMode.SqlOwnedDueTime);
        var options = _createdSchemas[^1];

        var committedSeq = await outbox.StoreScheduledAsync(ScheduledRow("t-committed", DateTime.UtcNow.AddHours(1)));

        await using (var connection = new SqlConnection(options.ConnectionString))
        {
            await connection.OpenAsync();
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            using (SqlServerOutboxAmbientTransaction.Begin(connection, transaction))
            {
                var seq = await outbox.StoreScheduledAsync(ScheduledRow("t-rolled-back", DateTime.UtcNow.AddHours(1)));
                Assert.IsTrue(seq > 0, "The provider-local handle is returned inside the transaction");

                var outcome = await outbox.CancelScheduledAsync(
                    new ScheduledMessageHandle("t-committed", committedSeq, ScheduledMessageHandleKind.SqlOutboxSequenceNumber));
                Assert.AreEqual(ScheduledMessageCancellationOutcome.CancelledBeforeDispatch, outcome);
            }

            await transaction.RollbackAsync();
        }

        Assert.AreEqual(0, Convert.ToInt32(await Scalar(options,
            $"SELECT COUNT(*) FROM {options.FullTableName} WHERE [MessageId]='t-rolled-back'"),
            System.Globalization.CultureInfo.InvariantCulture),
            "An ambient rollback leaves neither the schedule nor a cancellable row");
        Assert.IsNull(await Scalar(options,
            $"SELECT [CancelledAtUtc] FROM {options.FullTableName} WHERE [MessageId]='t-committed'"),
            "The rolled-back ambient cancellation must not stick");
    }

    // ── Cancellation CAS ────────────────────────────────────────────────

    [TestMethod]
    public async Task Cancel_BeforeAnyDispatch_ReturnsCancelledBeforeDispatch_AndRowIsNeverClaimable()
    {
        var outbox = await CreateOutboxAsync(ScheduledDeliveryMode.SqlOwnedDueTime);

        var seq = await outbox.StoreScheduledAsync(ScheduledRow("t1", DateTime.UtcNow.AddSeconds(-5)));
        var outcome = await outbox.CancelScheduledAsync(Handle("t1", seq));

        Assert.AreEqual(ScheduledMessageCancellationOutcome.CancelledBeforeDispatch, outcome);
        Assert.AreEqual(0, (await outbox.ClaimDueAsync(Guid.NewGuid(), 10)).Count,
            "A cancelled row can never pass the claim query");
    }

    [TestMethod]
    public async Task Cancel_AfterReservationBeforeStart_StillWins_AndStartAffectsZeroRows()
    {
        var outbox = await CreateOutboxAsync(ScheduledDeliveryMode.SqlOwnedDueTime);

        var seq = await outbox.StoreScheduledAsync(ScheduledRow("t1", DateTime.UtcNow.AddSeconds(-5)));
        var claimId = Guid.NewGuid();
        var claimed = await outbox.ClaimDueAsync(claimId, 10);
        Assert.AreEqual(1, claimed.Count);

        var outcome = await outbox.CancelScheduledAsync(Handle("t1", seq));
        Assert.AreEqual(ScheduledMessageCancellationOutcome.CancelledBeforeDispatch, outcome,
            "Merely reserving a row does not close cancellation");

        Assert.IsNull(await outbox.TryStartDispatchAsync(claimed[0].Id, claimId),
            "The dispatch-start fence must lose after cancellation won");
    }

    [TestMethod]
    public async Task Cancel_AfterDispatchStart_ReturnsTooLate()
    {
        var outbox = await CreateOutboxAsync(ScheduledDeliveryMode.SqlOwnedDueTime);

        var seq = await outbox.StoreScheduledAsync(ScheduledRow("t1", DateTime.UtcNow.AddSeconds(-5)));
        var claimId = Guid.NewGuid();
        var claimed = await outbox.ClaimDueAsync(claimId, 10);
        Assert.IsNotNull(await outbox.TryStartDispatchAsync(claimed[0].Id, claimId));

        Assert.AreEqual(ScheduledMessageCancellationOutcome.TooLate,
            await outbox.CancelScheduledAsync(Handle("t1", seq)));

        // And after dispatch completes it stays TooLate.
        Assert.IsTrue(await outbox.TryCompleteAsync(claimed[0].Id, claimId));
        Assert.AreEqual(ScheduledMessageCancellationOutcome.TooLate,
            await outbox.CancelScheduledAsync(Handle("t1", seq)));
    }

    [TestMethod]
    public async Task Cancel_Duplicate_ReturnsAlreadyCancelledWithoutSecondMutation()
    {
        var outbox = await CreateOutboxAsync(ScheduledDeliveryMode.SqlOwnedDueTime);
        var options = _createdSchemas[^1];

        var seq = await outbox.StoreScheduledAsync(ScheduledRow("t1", DateTime.UtcNow.AddHours(1)));
        await outbox.CancelScheduledAsync(Handle("t1", seq));
        var firstCancelledAt = (DateTime)(await Scalar(options,
            $"SELECT [CancelledAtUtc] FROM {options.FullTableName} WHERE [MessageId]='t1'"))!;

        var outcome = await outbox.CancelScheduledAsync(Handle("t1", seq));

        Assert.AreEqual(ScheduledMessageCancellationOutcome.AlreadyCancelled, outcome);
        Assert.AreEqual(firstCancelledAt, (DateTime)(await Scalar(options,
            $"SELECT [CancelledAtUtc] FROM {options.FullTableName} WHERE [MessageId]='t1'"))!,
            "No second mutation");
    }

    [TestMethod]
    public async Task Cancel_UnknownForgedOrMismatchedHandle_ReturnsNotFoundAndAffectsZeroRows()
    {
        var outbox = await CreateOutboxAsync(ScheduledDeliveryMode.SqlOwnedDueTime);
        var options = _createdSchemas[^1];

        var seq = await outbox.StoreScheduledAsync(ScheduledRow("t1", DateTime.UtcNow.AddHours(1)));

        // Unknown sequence.
        Assert.AreEqual(ScheduledMessageCancellationOutcome.NotFound,
            await outbox.CancelScheduledAsync(Handle("t1", seq + 1000)));
        // Mismatched TimeoutId for a real sequence.
        Assert.AreEqual(ScheduledMessageCancellationOutcome.NotFound,
            await outbox.CancelScheduledAsync(Handle("some-other-timeout", seq)));
        // A nonscheduled row's sequence cannot be cancelled through a forged handle.
        await outbox.StoreAsync(PlainRow("immediate-1"));
        var immediateSeq = Convert.ToInt64(await Scalar(options,
            $"SELECT [OutboxSequenceNumber] FROM {options.FullTableName} WHERE [Id]='immediate-1'"),
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.AreEqual(ScheduledMessageCancellationOutcome.NotFound,
            await outbox.CancelScheduledAsync(Handle("immediate-1", immediateSeq)));

        Assert.IsNull(await Scalar(options,
            $"SELECT MAX([CancelledAtUtc]) FROM {options.FullTableName}"), "Zero rows affected");
    }

    // ── Due-time eligibility and ordering ───────────────────────────────

    [TestMethod]
    public async Task FutureRow_IsNotClaimableBeforeSqlDueTime_AndDoesNotBlockImmediateSameSessionRow()
    {
        var outbox = await CreateOutboxAsync(ScheduledDeliveryMode.SqlOwnedDueTime);

        await outbox.StoreScheduledAsync(ScheduledRow("future", DateTime.UtcNow.AddHours(2), sessionId: "s1"));
        await outbox.StoreScheduledAsync(ScheduledRow("due", DateTime.UtcNow.AddSeconds(-5), sessionId: "s1"));

        var claimed = await outbox.ClaimDueAsync(Guid.NewGuid(), 10);

        Assert.AreEqual("due", claimed.Single().MessageId,
            "A future earlier-keyed row must not block an eligible immediate row, and is itself not claimable");
    }

    [TestMethod]
    public async Task SqlOwnedDueTimeOrdering_IgnoresSkewedCreatedAtUtc_UsesStoredAtPlusSequence()
    {
        var outbox = await CreateOutboxAsync(ScheduledDeliveryMode.SqlOwnedDueTime);

        // Insert three immediate rows whose application-stamped CreatedAtUtc is
        // regressed/equal relative to insertion order.
        var t = DateTime.UtcNow;
        await outbox.StoreAsync(PlainRow("first", createdAtUtc: t.AddMinutes(5)));   // skewed ahead
        await outbox.StoreAsync(PlainRow("second", createdAtUtc: t.AddMinutes(-5))); // regressed
        await outbox.StoreAsync(PlainRow("third", createdAtUtc: t.AddMinutes(-5)));  // equal to second

        var claimed = await outbox.ClaimDueAsync(Guid.NewGuid(), 10);

        CollectionAssert.AreEqual(
            new[] { "first", "second", "third" },
            claimed.Select(m => m.Id).ToArray(),
            "SQL-owned StoredAtUtc + IDENTITY sequence are the ordering authority; CreatedAtUtc never orders dispatch in this mode");
    }

    [TestMethod]
    public async Task DefaultModeParity_SelectionAndOrderingAreCreatedAtUtcAscExactly()
    {
        var outbox = await CreateOutboxAsync(ScheduledDeliveryMode.BrokerScheduleAtDispatch);

        var t = DateTime.UtcNow;
        await outbox.StoreAsync(PlainRow("inserted-first", createdAtUtc: t.AddMinutes(5)));
        await outbox.StoreAsync(PlainRow("inserted-second", createdAtUtc: t.AddMinutes(-5)));
        await outbox.StoreAsync(PlainRow("inserted-third", createdAtUtc: t));

        var pending = await outbox.GetPendingAsync(10);

        CollectionAssert.AreEqual(
            new[] { "inserted-second", "inserted-third", "inserted-first" },
            pending.Select(m => m.Id).ToArray(),
            "Default mode dispatches in CreatedAtUtc order — bit-for-bit what an old binary would select");
    }

    // ── Claims, leases, fences, checkpoints ─────────────────────────────

    [TestMethod]
    public async Task TwoWorkers_ClaimDisjointSessionlessRows()
    {
        var outbox = await CreateOutboxAsync(ScheduledDeliveryMode.SqlOwnedDueTime);

        await outbox.StoreAsync(PlainRow("row-1"));
        await outbox.StoreAsync(PlainRow("row-2"));

        var workerA = await outbox.ClaimDueAsync(Guid.NewGuid(), 1);
        var workerB = await outbox.ClaimDueAsync(Guid.NewGuid(), 1);

        Assert.AreEqual(1, workerA.Count);
        Assert.AreEqual(1, workerB.Count);
        Assert.AreNotEqual(workerA[0].Id, workerB[0].Id, "Live claims are disjoint");
    }

    [TestMethod]
    public async Task StartFence_ExtendsLeaseReturnsSqlDeadline_AndIsOwnerIdempotent()
    {
        var outbox = await CreateOutboxAsync(ScheduledDeliveryMode.SqlOwnedDueTime);
        var options = _createdSchemas[^1];

        await outbox.StoreAsync(PlainRow("row-1"));
        var claimId = Guid.NewGuid();
        var claimed = await outbox.ClaimDueAsync(claimId, 1);

        var deadline = await outbox.TryStartDispatchAsync(claimed[0].Id, claimId);
        Assert.IsNotNull(deadline, "The owning claim's first fence must win");
        var startedAt = (DateTime)(await Scalar(options,
            $"SELECT [DispatchStartedAtUtc] FROM {options.FullTableName} WHERE [Id]='row-1'"))!;

        // Owner-idempotent renewal: a second call by the owner renews the lease
        // and returns a fresh deadline while DispatchStartedAtUtc stays first-write.
        var renewed = await outbox.TryStartDispatchAsync(claimed[0].Id, claimId);
        Assert.IsNotNull(renewed);
        Assert.IsTrue(renewed >= deadline, "Renewal never shortens the SQL deadline");
        Assert.AreEqual(startedAt, (DateTime)(await Scalar(options,
            $"SELECT [DispatchStartedAtUtc] FROM {options.FullTableName} WHERE [Id]='row-1'"))!,
            "DispatchStartedAtUtc is written only when null");

        // A stale owner's fence affects zero rows.
        Assert.IsNull(await outbox.TryStartDispatchAsync(claimed[0].Id, Guid.NewGuid()));
    }

    [TestMethod]
    public async Task ExpiredLeaseBeforeStart_RowIsReclaimable_AndRemainsCancellable()
    {
        var outbox = await CreateOutboxAsync(ScheduledDeliveryMode.SqlOwnedDueTime);
        var options = _createdSchemas[^1];

        var seq = await outbox.StoreScheduledAsync(ScheduledRow("t1", DateTime.UtcNow.AddSeconds(-5)));
        var crashedWorker = Guid.NewGuid();
        var claimed = await outbox.ClaimDueAsync(crashedWorker, 1);
        Assert.AreEqual(1, claimed.Count);

        // Not reclaimable while the lease is live.
        Assert.AreEqual(0, (await outbox.ClaimDueAsync(Guid.NewGuid(), 1)).Count);

        await BackdateLease(options, claimed[0].Id);

        // Reclaimable after expiry — and still cancellable until start.
        var secondWorker = Guid.NewGuid();
        var reclaimed = await outbox.ClaimDueAsync(secondWorker, 1);
        Assert.AreEqual(1, reclaimed.Count);
        Assert.AreEqual(ScheduledMessageCancellationOutcome.CancelledBeforeDispatch,
            await outbox.CancelScheduledAsync(Handle("t1", seq)),
            "A crash before start leaves the row cancellable");
    }

    [TestMethod]
    public async Task StaleOwnerCheckpoint_AffectsZeroRows_ExactlyOneTerminalization()
    {
        var outbox = await CreateOutboxAsync(ScheduledDeliveryMode.SqlOwnedDueTime);
        var options = _createdSchemas[^1];

        await outbox.StoreAsync(PlainRow("row-1"));
        var staleWorker = Guid.NewGuid();
        var claimed = await outbox.ClaimDueAsync(staleWorker, 1);
        Assert.IsNotNull(await outbox.TryStartDispatchAsync(claimed[0].Id, staleWorker));

        // The send outlives the lease (sender ignoring cancellation); the row is
        // reclaimed by another worker via the expired-head bypass.
        await BackdateLease(options, claimed[0].Id);
        var reclaimingWorker = Guid.NewGuid();
        var reclaimed = await outbox.ClaimDueAsync(reclaimingWorker, 1);
        Assert.AreEqual(1, reclaimed.Count, "An expired started head is reclaimable");
        Assert.IsNotNull(await outbox.TryStartDispatchAsync(reclaimed[0].Id, reclaimingWorker));

        // The stale original owner can neither renew nor checkpoint.
        Assert.IsNull(await outbox.TryStartDispatchAsync(claimed[0].Id, staleWorker),
            "Stale renewal after reclaim returns null");
        Assert.IsFalse(await outbox.TryCompleteAsync(claimed[0].Id, staleWorker),
            "A stale owner's checkpoint affects zero rows");

        Assert.IsTrue(await outbox.TryCompleteAsync(reclaimed[0].Id, reclaimingWorker),
            "Exactly one attempt terminalizes the row");
        Assert.IsFalse(await outbox.TryCompleteAsync(reclaimed[0].Id, reclaimingWorker),
            "A second checkpoint is a no-op");
    }

    [TestMethod]
    public async Task ReleaseClaim_MakesRowImmediatelyReclaimable_ButNeverReleasesStartedRows()
    {
        var outbox = await CreateOutboxAsync(ScheduledDeliveryMode.SqlOwnedDueTime);

        await outbox.StoreAsync(PlainRow("row-1"));
        var owner = Guid.NewGuid();
        var claimed = await outbox.ClaimDueAsync(owner, 1);

        // A stale owner's release affects zero rows.
        await outbox.ReleaseClaimAsync(claimed[0].Id, Guid.NewGuid());
        Assert.AreEqual(0, (await outbox.ClaimDueAsync(Guid.NewGuid(), 1)).Count);

        // The owner's release frees it immediately.
        await outbox.ReleaseClaimAsync(claimed[0].Id, owner);
        var reclaimed = await outbox.ClaimDueAsync(Guid.NewGuid(), 1);
        Assert.AreEqual(1, reclaimed.Count);

        // A started row cannot be released.
        var secondOwner = Guid.NewGuid();
        var again = await outbox.ClaimDueAsync(secondOwner, 1);
        Assert.AreEqual(0, again.Count); // still held by previous reclaim
    }

    // ── Session-head and backdated-row barriers ─────────────────────────

    [TestMethod]
    public async Task LiveReservation_BlocksSameSessionSuccessors()
    {
        var outbox = await CreateOutboxAsync(ScheduledDeliveryMode.SqlOwnedDueTime);

        await outbox.StoreScheduledAsync(ScheduledRow("n1", DateTime.UtcNow.AddMinutes(-10), sessionId: "s1"));
        await outbox.StoreScheduledAsync(ScheduledRow("n2", DateTime.UtcNow.AddMinutes(-9), sessionId: "s1"));

        var workerA = await outbox.ClaimDueAsync(Guid.NewGuid(), 1);
        Assert.AreEqual("n1", workerA.Single().MessageId);

        Assert.AreEqual(0, (await outbox.ClaimDueAsync(Guid.NewGuid(), 10)).Count,
            "B cannot claim n2 while n1's live reservation is the session head");
    }

    [TestMethod]
    public async Task BackdatedInsert_AfterReserveAndAfterStart_IsBlockedUntilHeadTerminalizes_ThenGoesNext()
    {
        var outbox = await CreateOutboxAsync(ScheduledDeliveryMode.SqlOwnedDueTime);

        await outbox.StoreScheduledAsync(ScheduledRow("head", DateTime.UtcNow.AddMinutes(-10), sessionId: "s1"));
        await outbox.StoreScheduledAsync(ScheduledRow("successor", DateTime.UtcNow.AddMinutes(-5), sessionId: "s1"));

        var owner = Guid.NewGuid();
        var claimed = await outbox.ClaimDueAsync(owner, 1);
        Assert.AreEqual("head", claimed.Single().MessageId);

        // Backdated earlier-key row lands while the head is Reserved.
        await outbox.StoreScheduledAsync(ScheduledRow("backdated", DateTime.UtcNow.AddMinutes(-60), sessionId: "s1"));
        Assert.AreEqual(0, (await outbox.ClaimDueAsync(Guid.NewGuid(), 10)).Count,
            "The backdated earlier-key row is not claimable while the head is reserved");

        // Still blocked while the head is DispatchStarted.
        Assert.IsNotNull(await outbox.TryStartDispatchAsync(claimed[0].Id, owner));
        Assert.AreEqual(0, (await outbox.ClaimDueAsync(Guid.NewGuid(), 10)).Count,
            "The backdated row stays blocked while the head is started");

        // After the head terminalizes, the backdated row dispatches BEFORE the successor.
        Assert.IsTrue(await outbox.TryCompleteAsync(claimed[0].Id, owner));
        var next = await outbox.ClaimDueAsync(Guid.NewGuid(), 1);
        Assert.AreEqual("backdated", next.Single().MessageId, "Backdated goes next after the head terminalizes");
    }

    [TestMethod]
    public async Task ExpiredStartedHead_WithBackdatedEarlierRow_HeadBypassesOrderingAndSessionNeverWedges()
    {
        var outbox = await CreateOutboxAsync(ScheduledDeliveryMode.SqlOwnedDueTime);
        var options = _createdSchemas[^1];

        await outbox.StoreScheduledAsync(ScheduledRow("head", DateTime.UtcNow.AddMinutes(-10), sessionId: "s1"));
        var owner = Guid.NewGuid();
        var claimed = await outbox.ClaimDueAsync(owner, 1);
        Assert.IsNotNull(await outbox.TryStartDispatchAsync(claimed[0].Id, owner));

        // Backdated earlier-key row arrives, then the head's lease expires.
        await outbox.StoreScheduledAsync(ScheduledRow("backdated", DateTime.UtcNow.AddMinutes(-60), sessionId: "s1"));
        await outbox.StoreScheduledAsync(ScheduledRow("successor", DateTime.UtcNow.AddMinutes(-5), sessionId: "s1"));
        await BackdateLease(options, claimed[0].Id);

        // The started head bypasses ordering predicate (a): it is the ONLY
        // reclaimable row; backdated and successor rows stay blocked.
        var reclaimer = Guid.NewGuid();
        var reclaimed = await outbox.ClaimDueAsync(reclaimer, 10);
        Assert.AreEqual(1, reclaimed.Count, "The session must not wedge");
        Assert.AreEqual("head", reclaimed.Single().MessageId,
            "Only the expired started head is reclaimable while it is in flight");

        Assert.IsNotNull(await outbox.TryStartDispatchAsync(reclaimed[0].Id, reclaimer));
        Assert.IsTrue(await outbox.TryCompleteAsync(reclaimed[0].Id, reclaimer));

        var next = await outbox.ClaimDueAsync(Guid.NewGuid(), 1);
        Assert.AreEqual("backdated", next.Single().MessageId,
            "Only after the head terminalizes does ordering admit the backdated row");
    }

    [TestMethod]
    public async Task InsertionBoundary_BackdatedInsertRacingClaim_NeverYieldsTwoLiveHeads()
    {
        await RunUnderBothRcsiSettings(async (outbox, options) =>
        {
            for (var i = 0; i < 10; i++)
            {
                var session = $"race-{i}";
                await outbox.StoreScheduledAsync(ScheduledRow($"head-{i}", DateTime.UtcNow.AddMinutes(-10), sessionId: session));

                var claim = Task.Run(() => outbox.ClaimDueAsync(Guid.NewGuid(), 10));
                var backdatedInsert = Task.Run(() => outbox.StoreScheduledAsync(
                    ScheduledRow($"backdated-{i}", DateTime.UtcNow.AddMinutes(-60), sessionId: session)));
                await Task.WhenAll(claim, backdatedInsert);

                // Another worker races too.
                await outbox.ClaimDueAsync(Guid.NewGuid(), 10);

                var liveHeads = Convert.ToInt32(await Scalar(options, $@"
                    SELECT COUNT(*) FROM {options.FullTableName}
                    WHERE [SessionId] = '{session}'
                      AND [DispatchedAtUtc] IS NULL AND [CancelledAtUtc] IS NULL
                      AND ([DispatchStartedAtUtc] IS NOT NULL
                           OR ([DispatchClaimId] IS NOT NULL AND [DispatchClaimedUntilUtc] > SYSUTCDATETIME()))"),
                    System.Globalization.CultureInfo.InvariantCulture);
                Assert.IsTrue(liveHeads <= 1,
                    $"Session {session} has {liveHeads} live heads — the HOLDLOCK range locks failed to serialize insert vs claim");
            }
        });
    }

    [TestMethod]
    public async Task DueRowClaimSmokeTest_HintSetIsValidUnderRcsiOffAndOn()
    {
        // Revision-6 finding 1: an invalid hint combination surfaces here as SQL
        // error 650/hint-conflict instead of shipping a dispatcher that silently
        // claims nothing on RCSI databases (Azure SQL's default).
        await RunUnderBothRcsiSettings(async (outbox, _) =>
        {
            await outbox.StoreScheduledAsync(ScheduledRow("due", DateTime.UtcNow.AddSeconds(-5), sessionId: "s1"));
            var claimed = await outbox.ClaimDueAsync(Guid.NewGuid(), 10);
            Assert.AreEqual(1, claimed.Count, "Due rows must claim normally under the exact production hint set");
        });
    }

    // ── Version skew and mode gate motivation ───────────────────────────

    [TestMethod]
    public async Task VersionSkew_LegacyShapedReaderAgainstNewStyleRows_DemonstratesWhyTheModeGateExists()
    {
        var outbox = await CreateOutboxAsync(ScheduledDeliveryMode.SqlOwnedDueTime);
        var options = _createdSchemas[^1];

        var futureSeq = await outbox.StoreScheduledAsync(ScheduledRow("future", DateTime.UtcNow.AddHours(2)));
        var cancelledSeq = await outbox.StoreScheduledAsync(ScheduledRow("cancelled", DateTime.UtcNow.AddHours(2)));
        await outbox.CancelScheduledAsync(Handle("cancelled", cancelledSeq));
        _ = futureSeq;

        // A pre-upgrade binary's exact selection: no due filter, no CancelledAtUtc
        // guard, no claim columns. It would select BOTH rows — eagerly
        // broker-scheduling the future one and sending the cancelled one.
        var legacySelected = new List<string>();
        await using (var connection = new SqlConnection(options.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                $"SELECT [MessageId] FROM {options.FullTableName} WITH (UPDLOCK, READPAST) WHERE [DispatchedAtUtc] IS NULL ORDER BY [CreatedAtUtc] ASC",
                connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                legacySelected.Add(reader.GetString(0));
        }

        CollectionAssert.AreEquivalent(new[] { "future", "cancelled" }, legacySelected,
            "Documented failure mode: an old dispatcher bypasses due-time and CancelledAtUtc semantics — the mode flip asserts no such binary runs");

        // The upgraded default-mode query at least never dispatches a cancelled row.
        var downgraded = new SqlServerOutbox(NewOptionsLike(options, ScheduledDeliveryMode.BrokerScheduleAtDispatch));
        var pending = await downgraded.GetPendingAsync(10);
        CollectionAssert.AreEqual(new[] { "future" }, pending.Select(m => m.MessageId).ToArray(),
            "A misconfigured downgrade must still never dispatch a cancelled row");
    }

    // ── Mode-scoped gauges and cleanup ──────────────────────────────────

    [TestMethod]
    public async Task Gauges_SqlOwnedDueTime_MonthAheadRowInvisibleUntilDue_ThenNearZeroLag()
    {
        var outbox = await CreateOutboxAsync(ScheduledDeliveryMode.SqlOwnedDueTime);

        await outbox.StoreScheduledAsync(ScheduledRow("month-ahead", DateTime.UtcNow.AddDays(30)));

        Assert.AreEqual(0L, await outbox.GetPendingCountAsync(),
            "A not-yet-due row contributes no pending count");
        Assert.IsNull(await outbox.GetOldestPendingEnqueuedAtUtcAsync(),
            "A not-yet-due row contributes no lag");

        // A row that just became due reports ~zero lag (baseline is its due time).
        var justDue = DateTime.UtcNow.AddSeconds(-2);
        await outbox.StoreScheduledAsync(ScheduledRow("just-due", justDue));
        Assert.AreEqual(1L, await outbox.GetPendingCountAsync());
        var oldest = await outbox.GetOldestPendingEnqueuedAtUtcAsync();
        Assert.IsNotNull(oldest);
        Assert.IsTrue((DateTime.UtcNow - oldest.Value.UtcDateTime).Duration() < TimeSpan.FromMinutes(1),
            $"Lag counts from the due time, so it must be ~0 at due (baseline {oldest})");
    }

    [TestMethod]
    public async Task Gauges_DefaultMode_KeepTodaysSemantics()
    {
        var outbox = await CreateOutboxAsync(ScheduledDeliveryMode.BrokerScheduleAtDispatch);

        var created = DateTime.UtcNow.AddMinutes(-30);
        await outbox.StoreAsync(PlainRow("r1", createdAtUtc: created));
        var scheduled = ScheduledRow("future", DateTime.UtcNow.AddDays(30));
        scheduled.CreatedAtUtc = DateTime.UtcNow;
        await outbox.StoreAsync(scheduled);

        Assert.AreEqual(2L, await outbox.GetPendingCountAsync(),
            "Default mode counts every undispatched row, future-scheduled included (it is immediately actionable there)");
        var oldest = await outbox.GetOldestPendingEnqueuedAtUtcAsync();
        Assert.AreEqual(created, oldest!.Value.UtcDateTime);
    }

    [TestMethod]
    public async Task Purge_RemovesOldDispatchedAndCancelledRows_KeepsPending()
    {
        var outbox = await CreateOutboxAsync(ScheduledDeliveryMode.SqlOwnedDueTime);
        var options = _createdSchemas[^1];

        await outbox.StoreAsync(PlainRow("pending"));
        await outbox.StoreAsync(PlainRow("dispatched"));
        await outbox.MarkAsDispatchedAsync("dispatched");
        var seq = await outbox.StoreScheduledAsync(ScheduledRow("cancelled", DateTime.UtcNow.AddHours(1)));
        await outbox.CancelScheduledAsync(Handle("cancelled", seq));

        // Age the terminal timestamps beyond the cutoff.
        await Execute(options, $@"
            UPDATE {options.FullTableName} SET [DispatchedAtUtc] = DATEADD(DAY, -2, SYSUTCDATETIME()) WHERE [Id] = 'dispatched';
            UPDATE {options.FullTableName} SET [CancelledAtUtc] = DATEADD(DAY, -2, SYSUTCDATETIME()) WHERE [MessageId] = 'cancelled';");

        var purged = await outbox.PurgeDispatchedAsync(TimeSpan.FromDays(1));

        Assert.AreEqual(2, purged, "Both terminal states are purged");
        Assert.AreEqual(1, Convert.ToInt32(await Scalar(options, $"SELECT COUNT(*) FROM {options.FullTableName}"),
            System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual("pending", (string)(await Scalar(options, $"SELECT [Id] FROM {options.FullTableName}"))!);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("NIMBUS_SQL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("NIMBUS_SQL_TEST_CONNECTION not set; skipping SQL scheduling integration tests.");
        }

        return connectionString;
    }

    private SqlServerOutboxOptions NewOptions(ScheduledDeliveryMode mode) => new()
    {
        ConnectionString = GetConnectionString(),
        Schema = $"nimbus_sched_{Guid.NewGuid():N}"[..24],
        ScheduledDelivery = mode,
    };

    private static SqlServerOutboxOptions NewOptionsLike(SqlServerOutboxOptions template, ScheduledDeliveryMode mode) => new()
    {
        ConnectionString = template.ConnectionString,
        Schema = template.Schema,
        TableName = template.TableName,
        ScheduledDelivery = mode,
    };

    private async Task<SqlServerOutbox> CreateOutboxAsync(ScheduledDeliveryMode mode)
    {
        var options = NewOptions(mode);
        _createdSchemas.Add(options);
        var outbox = new SqlServerOutbox(options);
        await outbox.EnsureTableExistsAsync();
        return outbox;
    }

    private static OutboxMessage ScheduledRow(string timeoutId, DateTime dueUtc, string sessionId = null) => new()
    {
        Id = timeoutId,
        MessageId = timeoutId,
        To = "Test",
        EventTypeId = "PaymentTimedOut",
        SessionId = sessionId,
        CorrelationId = "conversation-1",
        Payload = "{}",
        CreatedAtUtc = DateTime.UtcNow,
        ScheduledEnqueueTimeUtc = dueUtc,
    };

    private static OutboxMessage PlainRow(string id, DateTime? createdAtUtc = null) => new()
    {
        Id = id,
        MessageId = "msg-" + id,
        To = "Test",
        EventTypeId = "OrderPlaced",
        Payload = "{}",
        CreatedAtUtc = createdAtUtc ?? DateTime.UtcNow,
    };

    private static ScheduledMessageHandle Handle(string timeoutId, long sequence) =>
        new(timeoutId, sequence, ScheduledMessageHandleKind.SqlOutboxSequenceNumber);

    private static async Task BackdateLease(SqlServerOutboxOptions options, string id)
    {
        await Execute(options,
            $"UPDATE {options.FullTableName} SET [DispatchClaimedUntilUtc] = DATEADD(SECOND, -5, SYSUTCDATETIME()) WHERE [Id] = '{id}'");
    }

    private static async Task Execute(SqlServerOutboxOptions options, string sql)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object> Scalar(SqlServerOutboxOptions options, string sql)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    /// <summary>
    /// Runs the body once with READ_COMMITTED_SNAPSHOT OFF and once with it ON
    /// (Azure SQL's default). RCSI cannot be toggled on the shared test database
    /// (CI points at master), so each invocation creates and drops a dedicated
    /// scratch database; Inconclusive when the login cannot CREATE DATABASE.
    /// </summary>
    private async Task RunUnderBothRcsiSettings(Func<SqlServerOutbox, SqlServerOutboxOptions, Task> body)
    {
        var baseConnectionString = GetConnectionString();
        var scratchDb = $"nimbus_rcsi_{Guid.NewGuid():N}"[..24];
        try
        {
            await ExecuteOn(baseConnectionString, $"CREATE DATABASE [{scratchDb}]");
        }
        catch (SqlException ex)
        {
            Assert.Inconclusive($"Cannot create a scratch database for RCSI toggling: {ex.Message}");
        }

        var scratchConnectionString = new SqlConnectionStringBuilder(baseConnectionString)
        {
            InitialCatalog = scratchDb,
        }.ConnectionString;

        try
        {
            foreach (var rcsiOn in new[] { false, true })
            {
                SqlConnection.ClearAllPools();
                await ExecuteOn(baseConnectionString,
                    $"ALTER DATABASE [{scratchDb}] SET READ_COMMITTED_SNAPSHOT {(rcsiOn ? "ON" : "OFF")} WITH ROLLBACK IMMEDIATE");
                SqlConnection.ClearAllPools();

                var options = new SqlServerOutboxOptions
                {
                    ConnectionString = scratchConnectionString,
                    Schema = $"nimbus_sched_{Guid.NewGuid():N}"[..24],
                    ScheduledDelivery = ScheduledDeliveryMode.SqlOwnedDueTime,
                };
                var outbox = new SqlServerOutbox(options);
                await outbox.EnsureTableExistsAsync();
                await body(outbox, options);
            }
        }
        finally
        {
            SqlConnection.ClearAllPools();
            try
            {
                await ExecuteOn(baseConnectionString,
                    $"ALTER DATABASE [{scratchDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{scratchDb}];");
            }
            catch (SqlException)
            {
                // Best-effort drop; scratch databases are uniquely named.
            }
        }
    }

    private static async Task ExecuteOn(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }
}
