using Microsoft.Data.SqlClient;
using NimBus.Core.Messages;
using NimBus.Core.Outbox;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Outbox.SqlServer
{
    /// <summary>
    /// SQL Server implementation of the transactional outbox, including the
    /// spec-025 scheduled-delivery companions: <see cref="IScheduledOutbox"/>
    /// (due-time rows with provider-local handles and a cancel CAS) and
    /// <see cref="IOutboxDispatchCoordinator"/> (claim/lease/fence/checkpoint
    /// dispatch protocol). Both companions are registered unconditionally but
    /// gate on <see cref="SqlServerOutboxOptions.ScheduledDelivery"/>.
    /// </summary>
    public class SqlServerOutbox : IOutbox, IOutboxCleanup, IOutboxMetricsQuery, IScheduledOutbox, IOutboxDispatchCoordinator
    {
        private const int SqlErrorDeadlockVictim = 1205;
        private readonly SqlServerOutboxOptions _options;

        public SqlServerOutbox(SqlServerOutboxOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            ValidateSqlIdentifier(_options.Schema, nameof(_options.Schema));
            ValidateSqlIdentifier(_options.TableName, nameof(_options.TableName));
            _options.ValidateLeaseOptions();
        }

        /// <summary>
        /// Ensures the outbox table exists. Call on startup if AutoCreateTable is enabled.
        /// Also runs an idempotent column migration so deployments that pre-date the
        /// W3C trace-context or scheduled-delivery (spec 025) columns gain them on next
        /// startup. The entire DDL batch runs under a session-scoped exclusive
        /// application lock so concurrent rolling-startup initializers serialize
        /// instead of racing the guards; adding the IDENTITY column may rewrite the
        /// table, so schedule first startup after this upgrade inside a maintenance
        /// window on very large outboxes.
        /// </summary>
        public async Task EnsureTableExistsAsync(CancellationToken cancellationToken = default)
        {
            var sql = $@"
                IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = @Schema)
                    EXEC('CREATE SCHEMA [{_options.Schema}]');

                IF NOT EXISTS (SELECT * FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = @Schema AND t.name = @TableName)
                CREATE TABLE {_options.FullTableName} (
                    [Id]                  NVARCHAR(128) NOT NULL PRIMARY KEY,
                    [MessageId]           NVARCHAR(512) NOT NULL,
                    [To]                  NVARCHAR(256) NULL,
                    [EventTypeId]         NVARCHAR(256) NULL,
                    [SessionId]           NVARCHAR(256) NULL,
                    [CorrelationId]       NVARCHAR(256) NULL,
                    [Payload]             NVARCHAR(MAX) NOT NULL,
                    [EnqueueDelayMinutes] INT NOT NULL DEFAULT 0,
                    [ScheduledEnqueueTimeUtc] DATETIME2 NULL,
                    [CreatedAtUtc]        DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    [DispatchedAtUtc]     DATETIME2 NULL,
                    [TraceParent]         NVARCHAR(55) NULL,
                    [TraceState]          NVARCHAR(256) NULL,
                    [OutboxSequenceNumber] BIGINT IDENTITY(1,1) NOT NULL,
                    [StoredAtUtc]         DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    [CancelledAtUtc]      DATETIME2 NULL,
                    [DispatchStartedAtUtc] DATETIME2 NULL,
                    [DispatchClaimId]     UNIQUEIDENTIFIER NULL,
                    [DispatchClaimedUntilUtc] DATETIME2 NULL,
                    [EffectiveDueAtUtc]   AS COALESCE([ScheduledEnqueueTimeUtc], [StoredAtUtc]) PERSISTED,
                    INDEX IX_OutboxMessages_Pending NONCLUSTERED ([DispatchedAtUtc], [CreatedAtUtc]) WHERE [DispatchedAtUtc] IS NULL
                );

                IF COL_LENGTH('{_options.FullTableName}', 'TraceParent') IS NULL
                    ALTER TABLE {_options.FullTableName} ADD [TraceParent] NVARCHAR(55) NULL;

                IF COL_LENGTH('{_options.FullTableName}', 'TraceState') IS NULL
                    ALTER TABLE {_options.FullTableName} ADD [TraceState] NVARCHAR(256) NULL;

                IF COL_LENGTH('{_options.FullTableName}', 'To') IS NULL
                    ALTER TABLE {_options.FullTableName} ADD [To] NVARCHAR(256) NULL;

                -- Spec 025: additive scheduled-delivery columns, ignored by pre-upgrade
                -- binaries and never consulted by default-mode selection/ordering.
                IF COL_LENGTH('{_options.FullTableName}', 'OutboxSequenceNumber') IS NULL
                    ALTER TABLE {_options.FullTableName} ADD [OutboxSequenceNumber] BIGINT IDENTITY(1,1) NOT NULL;

                IF COL_LENGTH('{_options.FullTableName}', 'StoredAtUtc') IS NULL
                BEGIN
                    ALTER TABLE {_options.FullTableName} ADD [StoredAtUtc] DATETIME2 NULL;
                    -- Backfill from CreatedAtUtc (best available approximation); the
                    -- OutboxSequenceNumber tiebreak keeps ordering deterministic.
                    EXEC('UPDATE {_options.FullTableName} SET [StoredAtUtc] = [CreatedAtUtc] WHERE [StoredAtUtc] IS NULL');
                    EXEC('ALTER TABLE {_options.FullTableName} ALTER COLUMN [StoredAtUtc] DATETIME2 NOT NULL');
                    EXEC('ALTER TABLE {_options.FullTableName} ADD CONSTRAINT [DF_{_options.Schema}_{_options.TableName}_StoredAtUtc] DEFAULT SYSUTCDATETIME() FOR [StoredAtUtc]');
                END;

                IF COL_LENGTH('{_options.FullTableName}', 'CancelledAtUtc') IS NULL
                    ALTER TABLE {_options.FullTableName} ADD [CancelledAtUtc] DATETIME2 NULL;

                IF COL_LENGTH('{_options.FullTableName}', 'DispatchStartedAtUtc') IS NULL
                    ALTER TABLE {_options.FullTableName} ADD [DispatchStartedAtUtc] DATETIME2 NULL;

                IF COL_LENGTH('{_options.FullTableName}', 'DispatchClaimId') IS NULL
                    ALTER TABLE {_options.FullTableName} ADD [DispatchClaimId] UNIQUEIDENTIFIER NULL;

                IF COL_LENGTH('{_options.FullTableName}', 'DispatchClaimedUntilUtc') IS NULL
                    ALTER TABLE {_options.FullTableName} ADD [DispatchClaimedUntilUtc] DATETIME2 NULL;

                IF COL_LENGTH('{_options.FullTableName}', 'EffectiveDueAtUtc') IS NULL
                    EXEC('ALTER TABLE {_options.FullTableName} ADD [EffectiveDueAtUtc] AS COALESCE([ScheduledEnqueueTimeUtc], [StoredAtUtc]) PERSISTED');

                IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UX_{_options.TableName}_OutboxSequenceNumber' AND object_id = OBJECT_ID('{_options.FullTableName}'))
                    EXEC('CREATE UNIQUE NONCLUSTERED INDEX [UX_{_options.TableName}_OutboxSequenceNumber] ON {_options.FullTableName} ([OutboxSequenceNumber])');

                -- The session-ordering index the HOLDLOCK session subqueries seek
                -- through; its key order defines the ranges whose locks cover a
                -- backdated row's insertion point.
                IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{_options.TableName}_SessionOrdering' AND object_id = OBJECT_ID('{_options.FullTableName}'))
                    EXEC('CREATE NONCLUSTERED INDEX [IX_{_options.TableName}_SessionOrdering] ON {_options.FullTableName} ([SessionId], [EffectiveDueAtUtc], [OutboxSequenceNumber]) INCLUDE ([DispatchedAtUtc], [CancelledAtUtc], [DispatchStartedAtUtc], [DispatchClaimId], [DispatchClaimedUntilUtc])');

                IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{_options.TableName}_Dispatchable' AND object_id = OBJECT_ID('{_options.FullTableName}'))
                    EXEC('CREATE NONCLUSTERED INDEX [IX_{_options.TableName}_Dispatchable] ON {_options.FullTableName} ([EffectiveDueAtUtc], [OutboxSequenceNumber]) INCLUDE ([SessionId], [ScheduledEnqueueTimeUtc], [StoredAtUtc], [DispatchStartedAtUtc], [DispatchClaimId], [DispatchClaimedUntilUtc]) WHERE [DispatchedAtUtc] IS NULL AND [CancelledAtUtc] IS NULL');";

            await using var connection = new SqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            // Session-scoped exclusive applock: concurrent rolling-startup
            // initializers serialize here, so every guard above is evaluated by
            // exactly one session at a time (spec 025).
            var lockResource = $"NimBus.Outbox:{_options.Schema}.{_options.TableName}";
            await using (var acquire = new SqlCommand(
                "DECLARE @r INT; EXEC @r = sp_getapplock @Resource = @Resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = @LockTimeout; SELECT @r;",
                connection))
            {
                acquire.Parameters.AddWithValue("@Resource", lockResource);
                acquire.Parameters.AddWithValue("@LockTimeout", (int)TimeSpan.FromMinutes(2).TotalMilliseconds);
                var lockResult = (int)(await acquire.ExecuteScalarAsync(cancellationToken) ?? -999);
                if (lockResult < 0)
                {
                    throw new InvalidOperationException(
                        $"Could not acquire the outbox schema applock '{lockResource}' (sp_getapplock returned {lockResult}). Another initializer may be stuck.");
                }
            }

            try
            {
                await using var command = new SqlCommand(sql, connection);
                command.CommandTimeout = (int)TimeSpan.FromMinutes(5).TotalSeconds;
                command.Parameters.AddWithValue("@Schema", _options.Schema);
                command.Parameters.AddWithValue("@TableName", _options.TableName);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                await using var release = new SqlCommand(
                    "EXEC sp_releaseapplock @Resource = @Resource, @LockOwner = 'Session';",
                    connection);
                release.Parameters.AddWithValue("@Resource", lockResource);
                try
                {
                    await release.ExecuteNonQueryAsync(CancellationToken.None);
                }
                catch (SqlException)
                {
                    // The session lock is released with the connection either way;
                    // an explicit-release failure must not mask the DDL outcome.
                }
            }
        }

        public async Task StoreAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            var sql = $@"
                INSERT INTO {_options.FullTableName}
                    ([Id], [MessageId], [To], [EventTypeId], [SessionId], [CorrelationId], [Payload], [EnqueueDelayMinutes], [ScheduledEnqueueTimeUtc], [CreatedAtUtc], [TraceParent], [TraceState])
                VALUES
                    (@Id, @MessageId, @To, @EventTypeId, @SessionId, @CorrelationId, @Payload, @EnqueueDelayMinutes, @ScheduledEnqueueTimeUtc, @CreatedAtUtc, @TraceParent, @TraceState)";

            var ambient = SqlServerOutboxAmbientTransaction.Current;
            if (ambient.HasValue)
            {
                await using var ambientCommand = new SqlCommand(sql, ambient.Value.Connection, ambient.Value.Transaction);
                AddOutboxMessageParameters(ambientCommand, message);
                await ambientCommand.ExecuteNonQueryAsync(cancellationToken);
                return;
            }

            await using var connection = new SqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(sql, connection);
            AddOutboxMessageParameters(command, message);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // 12 parameters per row; 100 rows = 1,200 parameters, comfortably under
        // SQL Server's 2,100-per-command limit. Matches the batch size used by
        // MarkAsDispatchedAsync.
        internal const int InsertBatchSize = 100;

        public async Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken = default)
        {
            var list = messages as IReadOnlyList<OutboxMessage> ?? new List<OutboxMessage>(messages);
            if (list.Count == 0)
            {
                return;
            }

            var ambient = SqlServerOutboxAmbientTransaction.Current;
            if (ambient.HasValue)
            {
                for (var offset = 0; offset < list.Count; offset += InsertBatchSize)
                {
                    var count = Math.Min(InsertBatchSize, list.Count - offset);
                    await using var ambientCommand = CreateBatchInsertCommand(ambient.Value.Connection, ambient.Value.Transaction, list, offset, count);
                    await ambientCommand.ExecuteNonQueryAsync(cancellationToken);
                }
                return;
            }

            await using var connection = new SqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

            try
            {
                for (var offset = 0; offset < list.Count; offset += InsertBatchSize)
                {
                    var count = Math.Min(InsertBatchSize, list.Count - offset);
                    await using var command = CreateBatchInsertCommand(connection, transaction, list, offset, count);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        /// <summary>
        /// Builds one multi-row INSERT for <paramref name="count"/> messages
        /// starting at <paramref name="offset"/>, with per-row suffixed parameter
        /// names. One round-trip per <see cref="InsertBatchSize"/> rows instead of
        /// one per message.
        /// </summary>
        internal SqlCommand CreateBatchInsertCommand(SqlConnection connection, SqlTransaction transaction, IReadOnlyList<OutboxMessage> messages, int offset, int count)
        {
            var command = new SqlCommand
            {
                Connection = connection,
                Transaction = transaction,
            };

            var rows = new string[count];
            for (var i = 0; i < count; i++)
            {
                var suffix = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                rows[i] = $"(@Id{suffix}, @MessageId{suffix}, @To{suffix}, @EventTypeId{suffix}, @SessionId{suffix}, @CorrelationId{suffix}, @Payload{suffix}, @EnqueueDelayMinutes{suffix}, @ScheduledEnqueueTimeUtc{suffix}, @CreatedAtUtc{suffix}, @TraceParent{suffix}, @TraceState{suffix})";
                AddOutboxMessageParameters(command, messages[offset + i], suffix);
            }

            command.CommandText = $@"
                INSERT INTO {_options.FullTableName}
                    ([Id], [MessageId], [To], [EventTypeId], [SessionId], [CorrelationId], [Payload], [EnqueueDelayMinutes], [ScheduledEnqueueTimeUtc], [CreatedAtUtc], [TraceParent], [TraceState])
                VALUES
                    {string.Join(",\n                    ", rows)}";

            return command;
        }

        public async Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default)
        {
            // Default-mode selection and ordering are bit-for-bit today's behavior
            // (CreatedAtUtc ASC): an upgraded default-mode dispatcher picks exactly
            // the row an old binary would. The single addition is the CancelledAtUtc
            // guard — vacuously true for anything default mode can produce, present
            // solely so a misconfigured mode-downgrade after cancellations cannot
            // dispatch a cancelled row (spec 025).
            var sql = $@"
                SELECT TOP (@BatchSize) [Id], [MessageId], [To], [EventTypeId], [SessionId], [CorrelationId], [Payload], [EnqueueDelayMinutes], [CreatedAtUtc], [ScheduledEnqueueTimeUtc], [TraceParent], [TraceState]
                FROM {_options.FullTableName} WITH (UPDLOCK, READPAST)
                WHERE [DispatchedAtUtc] IS NULL AND [CancelledAtUtc] IS NULL
                ORDER BY [CreatedAtUtc] ASC";

            var result = new List<OutboxMessage>();

            await using var connection = new SqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@BatchSize", batchSize);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new OutboxMessage
                {
                    Id = reader.GetString(0),
                    MessageId = reader.GetString(1),
                    To = reader.IsDBNull(2) ? null : reader.GetString(2),
                    EventTypeId = reader.IsDBNull(3) ? null : reader.GetString(3),
                    SessionId = reader.IsDBNull(4) ? null : reader.GetString(4),
                    CorrelationId = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Payload = reader.GetString(6),
                    EnqueueDelayMinutes = reader.GetInt32(7),
                    CreatedAtUtc = reader.GetDateTime(8),
                    ScheduledEnqueueTimeUtc = reader.IsDBNull(9) ? null : reader.GetDateTime(9),
                    DispatchedAtUtc = null,
                    TraceParent = reader.IsDBNull(10) ? null : reader.GetString(10),
                    TraceState = reader.IsDBNull(11) ? null : reader.GetString(11)
                });
            }

            return result;
        }

        public async Task MarkAsDispatchedAsync(string id, CancellationToken cancellationToken = default)
        {
            var sql = $@"UPDATE {_options.FullTableName} SET [DispatchedAtUtc] = SYSUTCDATETIME() WHERE [Id] = @Id AND [DispatchedAtUtc] IS NULL";

            await using var connection = new SqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@Id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task MarkAsDispatchedAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            // Use a batched approach with parameterized IN clause
            var idList = new List<string>(ids);
            for (int i = 0; i < idList.Count; i += 100)
            {
                var batch = idList.GetRange(i, Math.Min(100, idList.Count - i));
                var paramNames = new string[batch.Count];
                var sql = $"UPDATE {_options.FullTableName} SET [DispatchedAtUtc] = SYSUTCDATETIME() WHERE [DispatchedAtUtc] IS NULL AND [Id] IN (";

                await using var command = new SqlCommand();
                command.Connection = connection;

                for (int j = 0; j < batch.Count; j++)
                {
                    paramNames[j] = $"@Id{j}";
                    command.Parameters.AddWithValue(paramNames[j], batch[j]);
                }

                command.CommandText = sql + string.Join(", ", paramNames) + ")";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        public async Task<int> PurgeDispatchedAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
        {
            // Terminal cleanup covers both terminal states (spec 025): dispatched
            // rows by DispatchedAtUtc and cancelled rows by CancelledAtUtc. The API
            // name is retained for compatibility.
            var sql = $@"DELETE FROM {_options.FullTableName}
                WHERE ([DispatchedAtUtc] IS NOT NULL AND [DispatchedAtUtc] < @CutoffTime)
                   OR ([CancelledAtUtc] IS NOT NULL AND [DispatchedAtUtc] IS NULL AND [CancelledAtUtc] < @CutoffTime)";

            await using var connection = new SqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@CutoffTime", DateTime.UtcNow.Subtract(olderThan));
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // Pending/lag metrics are mode-scoped (spec 025, revision-4 finding 4):
        // in SqlOwnedDueTime only dispatch-eligible rows count and the lag
        // baseline is the persisted EffectiveDueAtUtc, so a month-ahead timeout
        // contributes nothing until due and ~0 lag at its due time. Default mode
        // keeps today's queries unchanged — future rows are immediately
        // actionable there, so their storage age is genuine lag.
        public async Task<long> GetPendingCountAsync(CancellationToken cancellationToken = default)
        {
            var sql = _options.ScheduledDelivery == ScheduledDeliveryMode.SqlOwnedDueTime
                ? $@"SELECT COUNT_BIG(*) FROM {_options.FullTableName}
                     WHERE [DispatchedAtUtc] IS NULL AND [CancelledAtUtc] IS NULL
                       AND ([ScheduledEnqueueTimeUtc] IS NULL OR [ScheduledEnqueueTimeUtc] <= SYSUTCDATETIME())"
                : $@"SELECT COUNT_BIG(*) FROM {_options.FullTableName} WHERE [DispatchedAtUtc] IS NULL";

            await using var connection = new SqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(sql, connection);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is long count ? count : 0;
        }

        public async Task<DateTimeOffset?> GetOldestPendingEnqueuedAtUtcAsync(CancellationToken cancellationToken = default)
        {
            var sql = _options.ScheduledDelivery == ScheduledDeliveryMode.SqlOwnedDueTime
                ? $@"SELECT MIN([EffectiveDueAtUtc]) FROM {_options.FullTableName}
                     WHERE [DispatchedAtUtc] IS NULL AND [CancelledAtUtc] IS NULL
                       AND ([ScheduledEnqueueTimeUtc] IS NULL OR [ScheduledEnqueueTimeUtc] <= SYSUTCDATETIME())"
                : $@"SELECT TOP 1 [CreatedAtUtc] FROM {_options.FullTableName} WHERE [DispatchedAtUtc] IS NULL ORDER BY [CreatedAtUtc] ASC";

            await using var connection = new SqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(sql, connection);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is null || result is DBNull)
                return null;
            return new DateTimeOffset((DateTime)result, TimeSpan.Zero);
        }

        // ── IScheduledOutbox (spec 025) ─────────────────────────────────────

        /// <inheritdoc/>
        public async Task<long> StoreScheduledAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            if (message is null) throw new ArgumentNullException(nameof(message));
            EnsureSqlOwnedDueTimeMode(nameof(StoreScheduledAsync));
            if (!message.ScheduledEnqueueTimeUtc.HasValue)
                throw new ArgumentException("A scheduled outbox row requires ScheduledEnqueueTimeUtc.", nameof(message));

            var sql = $@"
                INSERT INTO {_options.FullTableName}
                    ([Id], [MessageId], [To], [EventTypeId], [SessionId], [CorrelationId], [Payload], [EnqueueDelayMinutes], [ScheduledEnqueueTimeUtc], [CreatedAtUtc], [TraceParent], [TraceState])
                OUTPUT INSERTED.[OutboxSequenceNumber]
                VALUES
                    (@Id, @MessageId, @To, @EventTypeId, @SessionId, @CorrelationId, @Payload, @EnqueueDelayMinutes, @ScheduledEnqueueTimeUtc, @CreatedAtUtc, @TraceParent, @TraceState)";

            var ambient = SqlServerOutboxAmbientTransaction.Current;
            if (ambient.HasValue)
            {
                await using var ambientCommand = new SqlCommand(sql, ambient.Value.Connection, ambient.Value.Transaction);
                AddOutboxMessageParameters(ambientCommand, message);
                return (long)(await ambientCommand.ExecuteScalarAsync(cancellationToken)
                    ?? throw new InvalidOperationException("The scheduled outbox insert returned no sequence number."));
            }

            await using var connection = new SqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(sql, connection);
            AddOutboxMessageParameters(command, message);
            return (long)(await command.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("The scheduled outbox insert returned no sequence number."));
        }

        /// <inheritdoc/>
        public async Task<ScheduledMessageCancellationOutcome> CancelScheduledAsync(ScheduledMessageHandle handle, CancellationToken cancellationToken = default)
        {
            if (handle is null) throw new ArgumentNullException(nameof(handle));
            EnsureSqlOwnedDueTimeMode(nameof(CancelScheduledAsync));
            handle.Validate(nameof(handle));
            if (handle.Kind != ScheduledMessageHandleKind.SqlOutboxSequenceNumber)
            {
                throw new InvalidOperationException(
                    $"A {handle.Kind} handle cannot be cancelled by the SQL outbox provider.");
            }

            // One parameterized conditional UPDATE decides the cancel-vs-dispatch-start
            // race (invariant 5); the classifying SELECT only names the loser's
            // outcome. The CAS matches sequence AND TimeoutId AND scheduled-ness, so
            // a forged/mistyped handle affects zero rows and returns NotFound.
            var sql = $@"
                UPDATE {_options.FullTableName}
                SET [CancelledAtUtc] = SYSUTCDATETIME()
                WHERE [OutboxSequenceNumber] = @SequenceNumber
                  AND [MessageId] = @TimeoutId
                  AND [ScheduledEnqueueTimeUtc] IS NOT NULL
                  AND [CancelledAtUtc] IS NULL
                  AND [DispatchStartedAtUtc] IS NULL
                  AND [DispatchedAtUtc] IS NULL;

                IF @@ROWCOUNT = 1
                    SELECT {(int)ScheduledMessageCancellationOutcome.CancelledBeforeDispatch};
                ELSE
                    SELECT CASE
                        WHEN NOT EXISTS (
                            SELECT 1 FROM {_options.FullTableName}
                            WHERE [OutboxSequenceNumber] = @SequenceNumber
                              AND [MessageId] = @TimeoutId
                              AND [ScheduledEnqueueTimeUtc] IS NOT NULL)
                            THEN {(int)ScheduledMessageCancellationOutcome.NotFound}
                        WHEN EXISTS (
                            SELECT 1 FROM {_options.FullTableName}
                            WHERE [OutboxSequenceNumber] = @SequenceNumber
                              AND [MessageId] = @TimeoutId
                              AND [CancelledAtUtc] IS NOT NULL)
                            THEN {(int)ScheduledMessageCancellationOutcome.AlreadyCancelled}
                        ELSE {(int)ScheduledMessageCancellationOutcome.TooLate}
                    END;";

            var ambient = SqlServerOutboxAmbientTransaction.Current;
            if (ambient.HasValue)
            {
                await using var ambientCommand = new SqlCommand(sql, ambient.Value.Connection, ambient.Value.Transaction);
                AddCancelParameters(ambientCommand, handle);
                return (ScheduledMessageCancellationOutcome)Convert.ToInt32(
                    await ambientCommand.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            }

            await using var connection = new SqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(sql, connection);
            AddCancelParameters(command, handle);
            return (ScheduledMessageCancellationOutcome)Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        }

        // ── IOutboxDispatchCoordinator (spec 025) ───────────────────────────

        /// <inheritdoc/>
        public bool DueTimeDispatchActive => _options.ScheduledDelivery == ScheduledDeliveryMode.SqlOwnedDueTime;

        /// <inheritdoc/>
        public TimeSpan UsableSendWindow => _options.SendLeaseDuration - _options.SendLeaseSafetyMargin;

        /// <inheritdoc/>
        public async Task<IReadOnlyList<OutboxMessage>> ClaimDueAsync(Guid claimId, int batchSize, CancellationToken cancellationToken = default)
        {
            EnsureSqlOwnedDueTimeMode(nameof(ClaimDueAsync));

            // One UPDATE ... OUTPUT autocommit statement: every lock it takes is held
            // until its implicit transaction commits. Candidate scan hints are
            // UPDLOCK, READPAST, READCOMMITTEDLOCK — a locking, skip-locked-rows hint
            // set valid under both lock-based READ COMMITTED and RCSI (revision-6
            // finding 1). Both session subqueries use HOLDLOCK (serializable
            // key-range locks through the session-ordering index) and never READPAST,
            // so an in-flight head is always seen and a backdated INSERT racing the
            // claim cannot produce two live heads in either commit order.
            var sql = $@"
                WITH Candidates AS (
                    SELECT TOP (@BatchSize) c.[Id], c.[DispatchClaimId], c.[DispatchClaimedUntilUtc]
                    FROM {_options.FullTableName} c WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK)
                    WHERE c.[DispatchedAtUtc] IS NULL
                      AND c.[CancelledAtUtc] IS NULL
                      AND c.[EffectiveDueAtUtc] <= SYSUTCDATETIME()
                      AND (c.[DispatchClaimId] IS NULL OR c.[DispatchClaimedUntilUtc] <= SYSUTCDATETIME())
                      AND (c.[SessionId] IS NULL OR (
                            -- (a) ordering, first claims only: a not-yet-started candidate
                            -- is blocked while an earlier-keyed non-terminal session row
                            -- exists in ANY claim state; a dispatch-started candidate (the
                            -- expired head being reclaimed) bypasses ordering entirely.
                            (c.[DispatchStartedAtUtc] IS NOT NULL OR NOT EXISTS (
                                SELECT 1 FROM {_options.FullTableName} p WITH (HOLDLOCK)
                                WHERE p.[SessionId] = c.[SessionId]
                                  AND p.[DispatchedAtUtc] IS NULL
                                  AND p.[CancelledAtUtc] IS NULL
                                  AND (p.[EffectiveDueAtUtc] < c.[EffectiveDueAtUtc]
                                       OR (p.[EffectiveDueAtUtc] = c.[EffectiveDueAtUtc]
                                           AND p.[OutboxSequenceNumber] < c.[OutboxSequenceNumber]))))
                            -- (b) durable session head: no OTHER session row is in flight
                            -- (started, any lease state; or reserved under a live claim),
                            -- regardless of ordering key. Excluding the candidate by Id is
                            -- what allows the expired head itself to be reclaimed.
                            AND NOT EXISTS (
                                SELECT 1 FROM {_options.FullTableName} h WITH (HOLDLOCK)
                                WHERE h.[SessionId] = c.[SessionId]
                                  AND h.[Id] <> c.[Id]
                                  AND h.[DispatchedAtUtc] IS NULL
                                  AND h.[CancelledAtUtc] IS NULL
                                  AND (h.[DispatchStartedAtUtc] IS NOT NULL
                                       OR (h.[DispatchClaimId] IS NOT NULL AND h.[DispatchClaimedUntilUtc] > SYSUTCDATETIME())))
                      ))
                    ORDER BY c.[EffectiveDueAtUtc] ASC, c.[OutboxSequenceNumber] ASC
                )
                UPDATE Candidates
                SET [DispatchClaimId] = @ClaimId,
                    [DispatchClaimedUntilUtc] = DATEADD(MILLISECOND, @LeaseMs, SYSUTCDATETIME())
                OUTPUT INSERTED.[Id];";

            List<string> claimedIds = new();
            try
            {
                await using var connection = new SqlConnection(_options.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using (var command = new SqlCommand(sql, connection))
                {
                    command.Parameters.AddWithValue("@BatchSize", batchSize);
                    command.Parameters.AddWithValue("@ClaimId", claimId);
                    command.Parameters.AddWithValue("@LeaseMs", (int)_options.SendLeaseDuration.TotalMilliseconds);
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        claimedIds.Add(reader.GetString(0));
                }

                if (claimedIds.Count == 0)
                    return Array.Empty<OutboxMessage>();

                return await ReadRowsByIdAsync(connection, claimedIds, cancellationToken);
            }
            catch (SqlException ex) when (ex.Number == SqlErrorDeadlockVictim)
            {
                // Documented dispatcher behavior: a claim deadlock is retried as an
                // ordinary empty round on the next poll.
                return Array.Empty<OutboxMessage>();
            }
        }

        /// <inheritdoc/>
        public async Task<DateTime?> TryStartDispatchAsync(string outboxMessageId, Guid claimId, CancellationToken cancellationToken = default)
        {
            EnsureSqlOwnedDueTimeMode(nameof(TryStartDispatchAsync));

            // Owner-idempotent start fence (revision-6 finding 4): DispatchStartedAtUtc
            // is written only when null; re-invocation by the owning claim renews the
            // lease and OUTPUTs a fresh SQL-computed deadline. Requires row ID + claim
            // owner + unexpired lease + not cancelled + not dispatched (invariant 9).
            var sql = $@"
                UPDATE {_options.FullTableName}
                SET [DispatchStartedAtUtc] = COALESCE([DispatchStartedAtUtc], SYSUTCDATETIME()),
                    [DispatchClaimedUntilUtc] = DATEADD(MILLISECOND, @LeaseMs, SYSUTCDATETIME())
                OUTPUT INSERTED.[DispatchClaimedUntilUtc]
                WHERE [Id] = @Id
                  AND [DispatchClaimId] = @ClaimId
                  AND [DispatchClaimedUntilUtc] > SYSUTCDATETIME()
                  AND [CancelledAtUtc] IS NULL
                  AND [DispatchedAtUtc] IS NULL;";

            await using var connection = new SqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@Id", outboxMessageId);
            command.Parameters.AddWithValue("@ClaimId", claimId);
            command.Parameters.AddWithValue("@LeaseMs", (int)_options.SendLeaseDuration.TotalMilliseconds);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is DateTime deadline ? deadline : null;
        }

        /// <inheritdoc/>
        public async Task<bool> TryCompleteAsync(string outboxMessageId, Guid claimId, CancellationToken cancellationToken = default)
        {
            EnsureSqlOwnedDueTimeMode(nameof(TryCompleteAsync));

            // Checkpoint exclusivity: conditioned on row ID + claim owner. A stale
            // owner (reclaimed after lease expiry) affects zero rows, so exactly one
            // attempt terminalizes the row (invariant 9).
            var sql = $@"
                UPDATE {_options.FullTableName}
                SET [DispatchedAtUtc] = SYSUTCDATETIME()
                WHERE [Id] = @Id
                  AND [DispatchClaimId] = @ClaimId
                  AND [DispatchedAtUtc] IS NULL;";

            await using var connection = new SqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@Id", outboxMessageId);
            command.Parameters.AddWithValue("@ClaimId", claimId);
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }

        /// <inheritdoc/>
        public async Task ReleaseClaimAsync(string outboxMessageId, Guid claimId, CancellationToken cancellationToken = default)
        {
            EnsureSqlOwnedDueTimeMode(nameof(ReleaseClaimAsync));

            // Only a not-yet-started owned claim is releasable; a started row keeps
            // its claim (the broker outcome may be ambiguous) and a stale owner's
            // release affects zero rows.
            var sql = $@"
                UPDATE {_options.FullTableName}
                SET [DispatchClaimId] = NULL,
                    [DispatchClaimedUntilUtc] = NULL
                WHERE [Id] = @Id
                  AND [DispatchClaimId] = @ClaimId
                  AND [DispatchStartedAtUtc] IS NULL
                  AND [DispatchedAtUtc] IS NULL;";

            await using var connection = new SqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@Id", outboxMessageId);
            command.Parameters.AddWithValue("@ClaimId", claimId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private async Task<IReadOnlyList<OutboxMessage>> ReadRowsByIdAsync(
            SqlConnection connection,
            IReadOnlyList<string> ids,
            CancellationToken cancellationToken)
        {
            var paramNames = new string[ids.Count];
            await using var command = new SqlCommand { Connection = connection };
            for (var i = 0; i < ids.Count; i++)
            {
                paramNames[i] = $"@Id{i}";
                command.Parameters.AddWithValue(paramNames[i], ids[i]);
            }

            command.CommandText = $@"
                SELECT [Id], [MessageId], [To], [EventTypeId], [SessionId], [CorrelationId], [Payload], [EnqueueDelayMinutes], [CreatedAtUtc], [ScheduledEnqueueTimeUtc], [TraceParent], [TraceState], [OutboxSequenceNumber], [StoredAtUtc], [DispatchStartedAtUtc], [DispatchClaimId], [DispatchClaimedUntilUtc]
                FROM {_options.FullTableName}
                WHERE [Id] IN ({string.Join(", ", paramNames)})
                ORDER BY [EffectiveDueAtUtc] ASC, [OutboxSequenceNumber] ASC";

            var result = new List<OutboxMessage>(ids.Count);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new OutboxMessage
                {
                    Id = reader.GetString(0),
                    MessageId = reader.GetString(1),
                    To = reader.IsDBNull(2) ? null : reader.GetString(2),
                    EventTypeId = reader.IsDBNull(3) ? null : reader.GetString(3),
                    SessionId = reader.IsDBNull(4) ? null : reader.GetString(4),
                    CorrelationId = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Payload = reader.GetString(6),
                    EnqueueDelayMinutes = reader.GetInt32(7),
                    CreatedAtUtc = reader.GetDateTime(8),
                    ScheduledEnqueueTimeUtc = reader.IsDBNull(9) ? null : reader.GetDateTime(9),
                    DispatchedAtUtc = null,
                    TraceParent = reader.IsDBNull(10) ? null : reader.GetString(10),
                    TraceState = reader.IsDBNull(11) ? null : reader.GetString(11),
                    OutboxSequenceNumber = reader.GetInt64(12),
                    StoredAtUtc = reader.IsDBNull(13) ? null : reader.GetDateTime(13),
                    DispatchStartedAtUtc = reader.IsDBNull(14) ? null : reader.GetDateTime(14),
                    DispatchClaimId = reader.IsDBNull(15) ? null : reader.GetGuid(15),
                    DispatchClaimedUntilUtc = reader.IsDBNull(16) ? null : reader.GetDateTime(16),
                });
            }

            return result;
        }

        private void EnsureSqlOwnedDueTimeMode(string operation)
        {
            if (_options.ScheduledDelivery != ScheduledDeliveryMode.SqlOwnedDueTime)
            {
                throw new InvalidOperationException(
                    $"{operation} requires SqlServerOutboxOptions.ScheduledDelivery = ScheduledDeliveryMode.SqlOwnedDueTime. " +
                    "The mode flip is the operator's assertion that no pre-upgrade dispatcher runs against the outbox table (spec 025 cutover, phase 2).");
            }
        }

        private static void AddCancelParameters(SqlCommand command, ScheduledMessageHandle handle)
        {
            command.Parameters.AddWithValue("@SequenceNumber", handle.SequenceNumber);
            command.Parameters.AddWithValue("@TimeoutId", handle.TimeoutId);
        }

        private static void ValidateSqlIdentifier(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"SQL identifier '{parameterName}' cannot be null or empty.", parameterName);
            // Allow characters valid in bracket-quoted SQL Server identifiers, but reject
            // characters that could escape the brackets or enable injection.
            if (value.Contains(']') || value.Contains('\'') || value.Contains(';') || value.Contains("--", StringComparison.Ordinal))
                throw new ArgumentException($"SQL identifier '{parameterName}' contains characters that are not allowed (], ', ;, or --).", parameterName);
        }

        private static void AddOutboxMessageParameters(SqlCommand command, OutboxMessage message, string suffix = "")
        {
            command.Parameters.AddWithValue($"@Id{suffix}", message.Id);
            command.Parameters.AddWithValue($"@MessageId{suffix}", message.MessageId);
            command.Parameters.AddWithValue($"@To{suffix}", (object)message.To ?? DBNull.Value);
            command.Parameters.AddWithValue($"@EventTypeId{suffix}", (object)message.EventTypeId ?? DBNull.Value);
            command.Parameters.AddWithValue($"@SessionId{suffix}", (object)message.SessionId ?? DBNull.Value);
            command.Parameters.AddWithValue($"@CorrelationId{suffix}", (object)message.CorrelationId ?? DBNull.Value);
            command.Parameters.AddWithValue($"@Payload{suffix}", message.Payload);
            command.Parameters.AddWithValue($"@EnqueueDelayMinutes{suffix}", message.EnqueueDelayMinutes);
            command.Parameters.AddWithValue($"@ScheduledEnqueueTimeUtc{suffix}", (object?)message.ScheduledEnqueueTimeUtc ?? DBNull.Value);
            command.Parameters.AddWithValue($"@CreatedAtUtc{suffix}", message.CreatedAtUtc);
            command.Parameters.AddWithValue($"@TraceParent{suffix}", (object)message.TraceParent ?? DBNull.Value);
            command.Parameters.AddWithValue($"@TraceState{suffix}", (object)message.TraceState ?? DBNull.Value);
        }
    }
}
