using Microsoft.Data.SqlClient;
using NimBus.Core.Outbox;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Outbox.SqlServer
{
    /// <summary>
    /// SQL Server implementation of the transactional outbox.
    /// </summary>
    public class SqlServerOutbox : IOutbox, IOutboxCleanup
    {
        private readonly SqlServerOutboxOptions _options;

        public SqlServerOutbox(SqlServerOutboxOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            ValidateSqlIdentifier(_options.Schema, nameof(_options.Schema));
            ValidateSqlIdentifier(_options.TableName, nameof(_options.TableName));
        }

        /// <summary>
        /// Ensures the outbox table exists. Call on startup if AutoCreateTable is enabled.
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
                    [EventTypeId]         NVARCHAR(256) NULL,
                    [SessionId]           NVARCHAR(256) NULL,
                    [CorrelationId]       NVARCHAR(256) NULL,
                    [Payload]             NVARCHAR(MAX) NOT NULL,
                    [EnqueueDelayMinutes] INT NOT NULL DEFAULT 0,
                    [ScheduledEnqueueTimeUtc] DATETIME2 NULL,
                    [CreatedAtUtc]        DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    [DispatchedAtUtc]     DATETIME2 NULL,
                    INDEX IX_OutboxMessages_Pending NONCLUSTERED ([DispatchedAtUtc], [CreatedAtUtc]) WHERE [DispatchedAtUtc] IS NULL
                );";

            await using var connection = new SqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@Schema", _options.Schema);
            command.Parameters.AddWithValue("@TableName", _options.TableName);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task StoreAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            var sql = $@"
                INSERT INTO {_options.FullTableName}
                    ([Id], [MessageId], [EventTypeId], [SessionId], [CorrelationId], [Payload], [EnqueueDelayMinutes], [ScheduledEnqueueTimeUtc], [CreatedAtUtc])
                VALUES
                    (@Id, @MessageId, @EventTypeId, @SessionId, @CorrelationId, @Payload, @EnqueueDelayMinutes, @ScheduledEnqueueTimeUtc, @CreatedAtUtc)";

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

        public async Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken = default)
        {
            var ambient = SqlServerOutboxAmbientTransaction.Current;
            if (ambient.HasValue)
            {
                foreach (var message in messages)
                {
                    var ambientSql = $@"
                        INSERT INTO {_options.FullTableName}
                            ([Id], [MessageId], [EventTypeId], [SessionId], [CorrelationId], [Payload], [EnqueueDelayMinutes], [CreatedAtUtc])
                        VALUES
                            (@Id, @MessageId, @EventTypeId, @SessionId, @CorrelationId, @Payload, @EnqueueDelayMinutes, @CreatedAtUtc)";

                    await using var ambientCommand = new SqlCommand(ambientSql, ambient.Value.Connection, ambient.Value.Transaction);
                    AddOutboxMessageParameters(ambientCommand, message);
                    await ambientCommand.ExecuteNonQueryAsync(cancellationToken);
                }
                return;
            }

            await using var connection = new SqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

            try
            {
                foreach (var message in messages)
                {
                    var sql = $@"
                        INSERT INTO {_options.FullTableName}
                            ([Id], [MessageId], [EventTypeId], [SessionId], [CorrelationId], [Payload], [EnqueueDelayMinutes], [CreatedAtUtc])
                        VALUES
                            (@Id, @MessageId, @EventTypeId, @SessionId, @CorrelationId, @Payload, @EnqueueDelayMinutes, @CreatedAtUtc)";

                    await using var command = new SqlCommand(sql, connection, transaction);
                    AddOutboxMessageParameters(command, message);
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

        public async Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default)
        {
            var sql = $@"
                SELECT TOP (@BatchSize) [Id], [MessageId], [EventTypeId], [SessionId], [CorrelationId], [Payload], [EnqueueDelayMinutes], [CreatedAtUtc], [ScheduledEnqueueTimeUtc]
                FROM {_options.FullTableName} WITH (UPDLOCK, READPAST)
                WHERE [DispatchedAtUtc] IS NULL
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
                    EventTypeId = reader.IsDBNull(2) ? null : reader.GetString(2),
                    SessionId = reader.IsDBNull(3) ? null : reader.GetString(3),
                    CorrelationId = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Payload = reader.GetString(5),
                    EnqueueDelayMinutes = reader.GetInt32(6),
                    CreatedAtUtc = reader.GetDateTime(7),
                    ScheduledEnqueueTimeUtc = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                    DispatchedAtUtc = null
                });
            }

            return result;
        }

        public async Task MarkAsDispatchedAsync(string id, CancellationToken cancellationToken = default)
        {
            var sql = $@"UPDATE {_options.FullTableName} SET [DispatchedAtUtc] = SYSUTCDATETIME() WHERE [Id] = @Id";

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
                var sql = $"UPDATE {_options.FullTableName} SET [DispatchedAtUtc] = SYSUTCDATETIME() WHERE [Id] IN (";

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
            var sql = $@"DELETE FROM {_options.FullTableName} WHERE [DispatchedAtUtc] IS NOT NULL AND [DispatchedAtUtc] < @CutoffTime";

            await using var connection = new SqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@CutoffTime", DateTime.UtcNow.Subtract(olderThan));
            return await command.ExecuteNonQueryAsync(cancellationToken);
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

        private static void AddOutboxMessageParameters(SqlCommand command, OutboxMessage message)
        {
            command.Parameters.AddWithValue("@Id", message.Id);
            command.Parameters.AddWithValue("@MessageId", message.MessageId);
            command.Parameters.AddWithValue("@EventTypeId", (object)message.EventTypeId ?? DBNull.Value);
            command.Parameters.AddWithValue("@SessionId", (object)message.SessionId ?? DBNull.Value);
            command.Parameters.AddWithValue("@CorrelationId", (object)message.CorrelationId ?? DBNull.Value);
            command.Parameters.AddWithValue("@Payload", message.Payload);
            command.Parameters.AddWithValue("@EnqueueDelayMinutes", message.EnqueueDelayMinutes);
            command.Parameters.AddWithValue("@ScheduledEnqueueTimeUtc", (object?)message.ScheduledEnqueueTimeUtc ?? DBNull.Value);
            command.Parameters.AddWithValue("@CreatedAtUtc", message.CreatedAtUtc);
        }
    }
}
