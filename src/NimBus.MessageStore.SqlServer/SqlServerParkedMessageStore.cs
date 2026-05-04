using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using NimBus.MessageStore.Abstractions;

namespace NimBus.MessageStore.SqlServer;

/// <summary>
/// SQL Server-backed implementation of <see cref="IParkedMessageStore"/>. Rows
/// live in <c>[schema].[ParkedMessages]</c> (created by migration
/// <c>0010_ParkedMessages.sql</c>). Idempotency is the natural-key uniqueness
/// constraint on <c>(EndpointId, MessageId)</c>; FIFO replay safety is the
/// uniqueness constraint on <c>(EndpointId, SessionKey, ParkSequence)</c>.
/// Park-sequence allocation goes through
/// <see cref="ISessionStateStore.GetNextDeferralSequenceAndIncrement"/> so the
/// counter stays consistent with the legacy session-state counter.
/// </summary>
public sealed class SqlServerParkedMessageStore : IParkedMessageStore
{
    private readonly SqlServerMessageStoreOptions _options;
    private readonly ISessionStateStore _sessionStateStore;
    private readonly string _schema;
    private readonly int _commandTimeout;

    public SqlServerParkedMessageStore(IOptions<SqlServerMessageStoreOptions> options, ISessionStateStore sessionStateStore)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _sessionStateStore = sessionStateStore ?? throw new ArgumentNullException(nameof(sessionStateStore));
        _schema = _options.Schema;
        _commandTimeout = _options.CommandTimeoutSeconds;
    }

    private SqlConnection Open()
    {
        var conn = new SqlConnection(_options.ConnectionString);
        conn.Open();
        return conn;
    }

    private string T(string table) => $"[{_schema}].[{table}]";

    public async Task<long> ParkAsync(ParkedMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Two-step park (per the design):
        //   1. Idempotent insert with a placeholder sequence guarded by the
        //      (EndpointId, MessageId) unique constraint. ROWCOUNT tells us
        //      whether this is a fresh park (1) or a duplicate (0).
        //   2. Allocate sequence and stamp it on a fresh park; on a duplicate
        //      return the existing sequence. Avoids wasting sequence numbers
        //      (and the audit-visible gaps that come with them).
        await using var conn = Open();

        var insertSql = $@"
INSERT INTO {T("ParkedMessages")} (
    EndpointId, SessionKey, ParkSequence, MessageId, EventId, EventTypeId,
    BlockingEventId, MessageEnvelopeJson, ParkedAtUtc)
SELECT @EndpointId, @SessionKey, @PlaceholderSequence, @MessageId, @EventId, @EventTypeId,
       @BlockingEventId, @MessageEnvelopeJson, @ParkedAtUtc
WHERE NOT EXISTS (
    SELECT 1 FROM {T("ParkedMessages")}
    WHERE EndpointId = @EndpointId AND MessageId = @MessageId);";

        // Use a per-session negative placeholder so the (Endpoint,Session,Seq)
        // uniqueness constraint never collides on concurrent first-time parks
        // for *different* sessions; the placeholder gets stamped over before
        // we leave this method, so observers never see -1.
        // Encode the SessionKey hash into the placeholder to avoid collisions
        // across sessions on the same endpoint.
        var placeholder = -1L - Math.Abs((long)StringComparer.Ordinal.GetHashCode(message.SessionKey ?? string.Empty));

        var rows = await conn.ExecuteAsync(new CommandDefinition(insertSql, new
        {
            message.EndpointId,
            message.SessionKey,
            PlaceholderSequence = placeholder,
            message.MessageId,
            message.EventId,
            message.EventTypeId,
            message.BlockingEventId,
            message.MessageEnvelopeJson,
            ParkedAtUtc = message.ParkedAtUtc == default ? DateTime.UtcNow : message.ParkedAtUtc,
        }, commandTimeout: _commandTimeout, cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (rows == 0)
        {
            // Duplicate park: return the existing sequence.
            var existingSeq = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
                $@"SELECT ParkSequence FROM {T("ParkedMessages")}
                   WHERE EndpointId = @EndpointId AND MessageId = @MessageId",
                new { message.EndpointId, message.MessageId },
                commandTimeout: _commandTimeout, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return existingSeq ?? 0;
        }

        // Fresh park — allocate the sequence and stamp it.
        var sequence = await _sessionStateStore.GetNextDeferralSequenceAndIncrement(
            message.EndpointId, message.SessionKey, cancellationToken).ConfigureAwait(false);

        await conn.ExecuteAsync(new CommandDefinition(
            $@"UPDATE {T("ParkedMessages")}
               SET ParkSequence = @Sequence
               WHERE EndpointId = @EndpointId AND MessageId = @MessageId;
               UPDATE {T("SessionStates")}
               SET ActiveParkCount = ActiveParkCount + 1, UpdatedAtUtc = SYSUTCDATETIME()
               WHERE EndpointId = @EndpointId AND SessionId = @SessionKey;",
            new { Sequence = sequence, message.EndpointId, message.MessageId, message.SessionKey },
            commandTimeout: _commandTimeout, cancellationToken: cancellationToken)).ConfigureAwait(false);

        message.ParkSequence = sequence;
        return sequence;
    }

    public async Task<IReadOnlyList<ParkedMessage>> GetActiveAsync(string endpointId, string sessionKey, long afterSequence, int limit, CancellationToken cancellationToken = default)
    {
        var pageSize = limit > 0 ? limit : 100;
        var sql = $@"
SELECT TOP (@PageSize)
    EndpointId, SessionKey, ParkSequence, MessageId, EventId, EventTypeId,
    BlockingEventId, MessageEnvelopeJson, ParkedAtUtc, ReplayedAtUtc, SkippedAtUtc,
    DeadLetteredAtUtc, DeadLetterReason, ReplayAttemptCount
FROM {T("ParkedMessages")} WITH (READPAST)
WHERE EndpointId = @EndpointId
  AND SessionKey = @SessionKey
  AND ParkSequence > @AfterSequence
  AND ReplayedAtUtc IS NULL
  AND SkippedAtUtc IS NULL
  AND DeadLetteredAtUtc IS NULL
ORDER BY ParkSequence ASC;";

        await using var conn = Open();
        var rows = await conn.QueryAsync<ParkedMessageRow>(new CommandDefinition(sql, new
        {
            EndpointId = endpointId,
            SessionKey = sessionKey,
            AfterSequence = afterSequence,
            PageSize = pageSize,
        }, commandTimeout: _commandTimeout, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(MapRow).ToList();
    }

    public async Task MarkReplayedAsync(string endpointId, string messageId, CancellationToken cancellationToken = default)
    {
        // Idempotent: only update active rows. Decrement the active-park
        // counter only on the row that actually transitioned to Replayed.
        var sql = $@"
DECLARE @SessionKey NVARCHAR(200);
UPDATE {T("ParkedMessages")}
SET    ReplayedAtUtc = SYSUTCDATETIME(),
       @SessionKey = SessionKey
WHERE  EndpointId = @EndpointId
  AND  MessageId = @MessageId
  AND  ReplayedAtUtc IS NULL
  AND  SkippedAtUtc IS NULL
  AND  DeadLetteredAtUtc IS NULL;

IF @@ROWCOUNT = 1 AND @SessionKey IS NOT NULL
BEGIN
    UPDATE {T("SessionStates")}
    SET ActiveParkCount = CASE WHEN ActiveParkCount > 0 THEN ActiveParkCount - 1 ELSE 0 END,
        UpdatedAtUtc = SYSUTCDATETIME()
    WHERE EndpointId = @EndpointId AND SessionId = @SessionKey;
END;";

        await using var conn = Open();
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { EndpointId = endpointId, MessageId = messageId },
            commandTimeout: _commandTimeout, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task MarkSkippedAsync(string endpointId, string sessionKey, IReadOnlyList<string> messageIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        if (messageIds.Count == 0) return;

        var sql = $@"
DECLARE @TransitionedCount INT;
UPDATE {T("ParkedMessages")}
SET    SkippedAtUtc = SYSUTCDATETIME()
WHERE  EndpointId = @EndpointId
  AND  SessionKey = @SessionKey
  AND  MessageId IN @MessageIds
  AND  ReplayedAtUtc IS NULL
  AND  SkippedAtUtc IS NULL
  AND  DeadLetteredAtUtc IS NULL;
SET @TransitionedCount = @@ROWCOUNT;

IF @TransitionedCount > 0
BEGIN
    UPDATE {T("SessionStates")}
    SET ActiveParkCount = CASE
                              WHEN ActiveParkCount > @TransitionedCount THEN ActiveParkCount - @TransitionedCount
                              ELSE 0
                          END,
        UpdatedAtUtc = SYSUTCDATETIME()
    WHERE EndpointId = @EndpointId AND SessionId = @SessionKey;
END;";

        await using var conn = Open();
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            EndpointId = endpointId,
            SessionKey = sessionKey,
            MessageIds = messageIds.ToArray(),
        }, commandTimeout: _commandTimeout, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> IncrementReplayAttemptAsync(string endpointId, string messageId, CancellationToken cancellationToken = default)
    {
        var sql = $@"
UPDATE {T("ParkedMessages")}
SET    ReplayAttemptCount = ReplayAttemptCount + 1
OUTPUT inserted.ReplayAttemptCount
WHERE  EndpointId = @EndpointId
  AND  MessageId = @MessageId;";

        await using var conn = Open();
        var result = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(sql,
            new { EndpointId = endpointId, MessageId = messageId },
            commandTimeout: _commandTimeout, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return result ?? 0;
    }

    public async Task MarkDeadLetteredAsync(string endpointId, string messageId, string reason, CancellationToken cancellationToken = default)
    {
        var sql = $@"
DECLARE @SessionKey NVARCHAR(200);
UPDATE {T("ParkedMessages")}
SET    DeadLetteredAtUtc = SYSUTCDATETIME(),
       DeadLetterReason = @Reason,
       @SessionKey = SessionKey
WHERE  EndpointId = @EndpointId
  AND  MessageId = @MessageId
  AND  ReplayedAtUtc IS NULL
  AND  SkippedAtUtc IS NULL
  AND  DeadLetteredAtUtc IS NULL;

IF @@ROWCOUNT = 1 AND @SessionKey IS NOT NULL
BEGIN
    UPDATE {T("SessionStates")}
    SET ActiveParkCount = CASE WHEN ActiveParkCount > 0 THEN ActiveParkCount - 1 ELSE 0 END,
        UpdatedAtUtc = SYSUTCDATETIME()
    WHERE EndpointId = @EndpointId AND SessionId = @SessionKey;
END;";

        await using var conn = Open();
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            EndpointId = endpointId,
            MessageId = messageId,
            Reason = reason,
        }, commandTimeout: _commandTimeout, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> CountActiveAsync(string endpointId, string sessionKey, CancellationToken cancellationToken = default)
    {
        var sql = $@"
SELECT COUNT_BIG(*)
FROM {T("ParkedMessages")}
WHERE EndpointId = @EndpointId
  AND SessionKey = @SessionKey
  AND ReplayedAtUtc IS NULL
  AND SkippedAtUtc IS NULL
  AND DeadLetteredAtUtc IS NULL;";

        await using var conn = Open();
        var count = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(sql,
            new { EndpointId = endpointId, SessionKey = sessionKey },
            commandTimeout: _commandTimeout, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return (int)(count ?? 0);
    }

    private static ParkedMessage MapRow(ParkedMessageRow row) => new()
    {
        EndpointId = row.EndpointId,
        SessionKey = row.SessionKey,
        ParkSequence = row.ParkSequence,
        MessageId = row.MessageId,
        EventId = row.EventId,
        EventTypeId = row.EventTypeId ?? string.Empty,
        BlockingEventId = row.BlockingEventId,
        MessageEnvelopeJson = row.MessageEnvelopeJson,
        ParkedAtUtc = row.ParkedAtUtc,
        ReplayedAtUtc = row.ReplayedAtUtc,
        SkippedAtUtc = row.SkippedAtUtc,
        DeadLetteredAtUtc = row.DeadLetteredAtUtc,
        DeadLetterReason = row.DeadLetterReason,
        ReplayAttemptCount = row.ReplayAttemptCount,
    };

    private sealed class ParkedMessageRow
    {
        public string EndpointId { get; set; } = string.Empty;
        public string SessionKey { get; set; } = string.Empty;
        public long ParkSequence { get; set; }
        public string MessageId { get; set; } = string.Empty;
        public string EventId { get; set; } = string.Empty;
        public string? EventTypeId { get; set; }
        public string? BlockingEventId { get; set; }
        public string MessageEnvelopeJson { get; set; } = string.Empty;
        public DateTime ParkedAtUtc { get; set; }
        public DateTime? ReplayedAtUtc { get; set; }
        public DateTime? SkippedAtUtc { get; set; }
        public DateTime? DeadLetteredAtUtc { get; set; }
        public string? DeadLetterReason { get; set; }
        public int ReplayAttemptCount { get; set; }
    }
}
