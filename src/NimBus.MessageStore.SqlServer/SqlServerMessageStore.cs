using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using NimBus.Core.Messages;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.SqlServer;

/// <summary>
/// SQL Server-backed implementation of every NimBus storage contract. Single class
/// mirrors the surface of CosmosDbClient and the four interfaces it satisfies via
/// <see cref="INimBusMessageStore"/>.
///
/// Schema layout: one table per concern with EndpointId as a discriminator
/// (rather than the per-endpoint container model used by Cosmos). Indexes target
/// the dominant access patterns: status counts per endpoint, recent-events lists,
/// and per-event/per-message lookups.
///
/// Idempotency: status uploads use MERGE on the natural key
/// (EndpointId, EventId, SessionId).
/// Concurrency: ROWVERSION on UnresolvedEvents enables optimistic conflict
/// detection if needed by future writers.
/// </summary>
public sealed class SqlServerMessageStore : INimBusMessageStore, IHeartbeatHistoryStore
{
    private readonly SqlServerMessageStoreOptions _options;
    private readonly string _schema;
    private readonly int _commandTimeout;
    private readonly SqlServerMetricsStore _metrics;
    private readonly SqlServerEventSchemaStore _eventSchemas;
    private readonly SqlServerAccessControlStore _accessControl;

    public SqlServerMessageStore(IOptions<SqlServerMessageStoreOptions> options)
    {
        _options = options.Value;
        _schema = _options.Schema;
        _commandTimeout = _options.CommandTimeoutSeconds;

        var context = new SqlServerStoreContext
        {
            Open = OpenAsync,
            Table = T,
            CommandTimeout = _commandTimeout,
        };
        _metrics = new SqlServerMetricsStore(context);
        _eventSchemas = new SqlServerEventSchemaStore(context);
        _accessControl = new SqlServerAccessControlStore(context);
    }

    private async Task<SqlConnection> OpenAsync()
    {
        var conn = new SqlConnection(_options.ConnectionString);
        try
        {
            await SqlServerExceptionTranslation.TranslateAsync(() => conn.OpenAsync()).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // Defensive bracket-quoting: SqlServerSchemaInitializer is the primary gate
    // for schema-name validation, but escape `]` here too so a misuse outside
    // the hosted service can't break out of the quoted identifier.
    private string T(string table) => $"[{_schema.Replace("]", "]]", StringComparison.Ordinal)}].[{table.Replace("]", "]]", StringComparison.Ordinal)}]";

    // ───────── Resolver state writes (status transitions) ─────────

    public Task<bool> UploadPendingMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        => UpsertStatus(eventId, sessionId, endpointId, "Pending", content);

    public Task<bool> UploadDeferredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        => UpsertStatus(eventId, sessionId, endpointId, "Deferred", content);

    public Task<bool> UploadFailedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        => UpsertStatus(eventId, sessionId, endpointId, "Failed", content);

    public Task<bool> UploadDeadletteredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        => UpsertStatus(eventId, sessionId, endpointId, "DeadLettered", content);

    public Task<bool> UploadUnsupportedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        => UpsertStatus(eventId, sessionId, endpointId, "Unsupported", content);

    public Task<bool> UploadSkippedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        => UpsertStatus(eventId, sessionId, endpointId, "Skipped", content);

    public Task<bool> UploadCompletedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        => UpsertStatus(eventId, sessionId, endpointId, "Completed", content);

    private async Task<bool> UpsertStatus(string eventId, string sessionId, string endpointId, string status, UnresolvedEvent content)
    {
        var sql = $@"
MERGE {T("UnresolvedEvents")} AS target
USING (SELECT @EventId AS EventId, @SessionId AS SessionId, @EndpointId AS EndpointId) AS source
ON target.EndpointId = source.EndpointId AND target.EventId = source.EventId
   AND ((target.SessionId IS NULL AND source.SessionId IS NULL) OR target.SessionId = source.SessionId)
WHEN MATCHED THEN UPDATE SET
    Status = @Status,
    UpdatedAtUtc = @UpdatedAt,
    EnqueuedTimeUtc = @EnqueuedTimeUtc,
    CorrelationId = @CorrelationId,
    EndpointRole = @EndpointRole,
    MessageType = @MessageType,
    RetryCount = @RetryCount,
    RetryLimit = @RetryLimit,
    LastMessageId = @LastMessageId,
    OriginatingMessageId = @OriginatingMessageId,
    ParentMessageId = @ParentMessageId,
    OriginatingFrom = @OriginatingFrom,
    Reason = @Reason,
    DeadLetterReason = @DeadLetterReason,
    DeadLetterErrorDescription = @DeadLetterErrorDescription,
    EventTypeId = @EventTypeId,
    ToAddress = @ToAddress,
    FromAddress = @FromAddress,
    QueueTimeMs = @QueueTimeMs,
    ProcessingTimeMs = @ProcessingTimeMs,
    CloudEventId = @CloudEventId,
    CloudEventSource = @CloudEventSource,
    CloudEventType = @CloudEventType,
    CloudEventSubject = @CloudEventSubject,
    PendingSubStatus = @PendingSubStatus,
    HandoffReason = @HandoffReason,
    ExternalJobId = @ExternalJobId,
    ExpectedBy = @ExpectedBy,
    MessageContentJson = @MessageContentJson,
    Deleted = 0
WHEN NOT MATCHED THEN INSERT (
    EventId, SessionId, EndpointId, Status, UpdatedAtUtc, EnqueuedTimeUtc, CorrelationId, EndpointRole,
    MessageType, RetryCount, RetryLimit, LastMessageId, OriginatingMessageId, ParentMessageId,
    OriginatingFrom, Reason, DeadLetterReason, DeadLetterErrorDescription, EventTypeId,
    ToAddress, FromAddress, QueueTimeMs, ProcessingTimeMs,
    CloudEventId, CloudEventSource, CloudEventType, CloudEventSubject,
    PendingSubStatus, HandoffReason, ExternalJobId, ExpectedBy,
    MessageContentJson)
VALUES (
    @EventId, @SessionId, @EndpointId, @Status, @UpdatedAt, @EnqueuedTimeUtc, @CorrelationId, @EndpointRole,
    @MessageType, @RetryCount, @RetryLimit, @LastMessageId, @OriginatingMessageId, @ParentMessageId,
    @OriginatingFrom, @Reason, @DeadLetterReason, @DeadLetterErrorDescription, @EventTypeId,
    @ToAddress, @FromAddress, @QueueTimeMs, @ProcessingTimeMs,
    @CloudEventId, @CloudEventSource, @CloudEventType, @CloudEventSubject,
    @PendingSubStatus, @HandoffReason, @ExternalJobId, @ExpectedBy,
    @MessageContentJson);";

        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(sql, new
        {
            EventId = eventId,
            SessionId = sessionId,
            EndpointId = endpointId,
            Status = status,
            UpdatedAt = DateTime.UtcNow,
            EnqueuedTimeUtc = content.EnqueuedTimeUtc,
            CorrelationId = content.CorrelationId,
            EndpointRole = content.EndpointRole.ToString(),
            MessageType = content.MessageType.ToString(),
            RetryCount = content.RetryCount,
            RetryLimit = content.RetryLimit,
            LastMessageId = content.LastMessageId,
            OriginatingMessageId = content.OriginatingMessageId,
            ParentMessageId = content.ParentMessageId,
            OriginatingFrom = content.OriginatingFrom,
            Reason = content.Reason,
            DeadLetterReason = content.DeadLetterReason,
            DeadLetterErrorDescription = content.DeadLetterErrorDescription,
            EventTypeId = content.EventTypeId,
            ToAddress = content.To,
            FromAddress = content.From,
            QueueTimeMs = content.QueueTimeMs,
            ProcessingTimeMs = content.ProcessingTimeMs,
            content.CloudEventId,
            content.CloudEventSource,
            content.CloudEventType,
            content.CloudEventSubject,
            PendingSubStatus = content.PendingSubStatus,
            HandoffReason = content.HandoffReason,
            ExternalJobId = content.ExternalJobId,
            ExpectedBy = content.ExpectedBy,
            MessageContentJson = JsonConvert.SerializeObject(content.MessageContent),
        }, commandTimeout: _commandTimeout);

        return rows > 0;
    }

    // ───────── Per-message persistence (StoreMessage / history) ─────────

    public async Task StoreMessage(MessageEntity message)
    {
        var sql = $@"
IF NOT EXISTS (SELECT 1 FROM {T("Messages")} WHERE EventId = @EventId AND MessageId = @MessageId)
INSERT INTO {T("Messages")} (
    EventId, MessageId, EndpointId, SessionId, CorrelationId, EventTypeId,
    OriginatingMessageId, ParentMessageId, FromAddress, ToAddress, OriginatingFrom, OriginalSessionId,
    MessageType, EndpointRole, EnqueuedTimeUtc, RetryCount, RetryLimit, DeferralSequence,
    QueueTimeMs, ProcessingTimeMs, CloudEventId, CloudEventSource, CloudEventType, CloudEventSubject,
    DeadLetterReason, DeadLetterErrorDescription, MessageContentJson)
VALUES (
    @EventId, @MessageId, @EndpointId, @SessionId, @CorrelationId, @EventTypeId,
    @OriginatingMessageId, @ParentMessageId, @FromAddress, @ToAddress, @OriginatingFrom, @OriginalSessionId,
    @MessageType, @EndpointRole, @EnqueuedTimeUtc, @RetryCount, @RetryLimit, @DeferralSequence,
    @QueueTimeMs, @ProcessingTimeMs, @CloudEventId, @CloudEventSource, @CloudEventType, @CloudEventSubject,
    @DeadLetterReason, @DeadLetterErrorDescription, @MessageContentJson);";

        await using var conn = await OpenAsync();
        await conn.ExecuteAsync(sql, new
        {
            message.EventId,
            message.MessageId,
            message.EndpointId,
            message.SessionId,
            message.CorrelationId,
            message.EventTypeId,
            message.OriginatingMessageId,
            message.ParentMessageId,
            FromAddress = message.From,
            ToAddress = message.To,
            message.OriginatingFrom,
            message.OriginalSessionId,
            MessageType = message.MessageType.ToString(),
            EndpointRole = message.EndpointRole.ToString(),
            message.EnqueuedTimeUtc,
            message.RetryCount,
            message.RetryLimit,
            message.DeferralSequence,
            message.QueueTimeMs,
            message.ProcessingTimeMs,
            message.CloudEventId,
            message.CloudEventSource,
            message.CloudEventType,
            message.CloudEventSubject,
            message.DeadLetterReason,
            message.DeadLetterErrorDescription,
            MessageContentJson = JsonConvert.SerializeObject(message.MessageContent),
        }, commandTimeout: _commandTimeout);
    }

    public async Task<MessageEntity> GetMessage(string eventId, string messageId)
    {
        await using var conn = await OpenAsync();
        var row = await conn.QueryFirstOrDefaultAsync(
            $"SELECT * FROM {T("Messages")} WHERE EventId = @EventId AND MessageId = @MessageId",
            new { EventId = eventId, MessageId = messageId }, commandTimeout: _commandTimeout);
        return row == null
            ? throw new MessageNotFoundException(eventId, messageId)
            : MapMessageRow(row);
    }

    public async Task<IEnumerable<MessageEntity>> GetEventHistory(string eventId)
    {
        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(
            $"SELECT * FROM {T("Messages")} WHERE EventId = @EventId ORDER BY EnqueuedTimeUtc",
            new { EventId = eventId }, commandTimeout: _commandTimeout);
        return rows.Select(MapMessageRow).ToList();
    }

    public async Task<MessageEntity> GetLatestEventRequestMessage(string eventId)
    {
        await using var conn = await OpenAsync();
        // Narrow to the request-bearing message types in SQL and order newest-first.
        // EventJson lives inside the serialized MessageContent column, so the
        // non-empty-payload check happens after mapping. Stream the reader
        // unbuffered so it stops at the first payload-bearing row instead of
        // materialising the full request history.
        var rows = conn.QueryUnbufferedAsync(
            $@"SELECT * FROM {T("Messages")}
                WHERE EventId = @EventId
                  AND MessageType IN ('EventRequest', 'ResubmissionRequest')
                ORDER BY EnqueuedTimeUtc DESC",
            new { EventId = eventId }, commandTimeout: _commandTimeout);

        await foreach (var row in rows)
        {
            var message = (MessageEntity)MapMessageRow(row);
            if (!string.IsNullOrEmpty(message.MessageContent?.EventContent?.EventJson))
            {
                return message;
            }
        }

        return null;
    }

    public async Task<MessageEntity> GetFailedMessage(string eventId, string endpointId)
    {
        await using var conn = await OpenAsync();
        var row = await conn.QueryFirstOrDefaultAsync(
            $@"SELECT TOP 1 m.* FROM {T("Messages")} m
                WHERE m.EventId = @EventId AND m.EndpointId = @EndpointId
                ORDER BY m.EnqueuedTimeUtc DESC",
            new { EventId = eventId, EndpointId = endpointId }, commandTimeout: _commandTimeout);
        return row == null ? throw new MessageNotFoundException(eventId) : MapMessageRow(row);
    }

    public Task<MessageEntity> GetDeadletteredMessage(string eventId, string endpointId)
        => GetFailedMessage(eventId, endpointId);

    public async Task RemoveStoredMessage(string eventId, string messageId)
    {
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync(
            $"DELETE FROM {T("Messages")} WHERE EventId = @EventId AND MessageId = @MessageId",
            new { EventId = eventId, MessageId = messageId }, commandTimeout: _commandTimeout);
    }

    private static MessageEntity MapMessageRow(dynamic row)
    {
        return new MessageEntity
        {
            EventId = row.EventId,
            MessageId = row.MessageId,
            EndpointId = row.EndpointId,
            SessionId = row.SessionId ?? string.Empty,
            CorrelationId = row.CorrelationId ?? string.Empty,
            EventTypeId = row.EventTypeId ?? string.Empty,
            OriginatingMessageId = row.OriginatingMessageId ?? string.Empty,
            ParentMessageId = row.ParentMessageId ?? string.Empty,
            From = row.FromAddress ?? string.Empty,
            To = row.ToAddress ?? string.Empty,
            OriginatingFrom = row.OriginatingFrom ?? string.Empty,
            OriginalSessionId = row.OriginalSessionId ?? string.Empty,
            MessageType = Enum.TryParse((string?)row.MessageType, out MessageType mt) ? mt : MessageType.EventRequest,
            EndpointRole = Enum.TryParse((string?)row.EndpointRole, out EndpointRole er) ? er : EndpointRole.Subscriber,
            EnqueuedTimeUtc = row.EnqueuedTimeUtc,
            RetryCount = row.RetryCount,
            RetryLimit = row.RetryLimit,
            DeferralSequence = row.DeferralSequence,
            QueueTimeMs = row.QueueTimeMs,
            ProcessingTimeMs = row.ProcessingTimeMs,
            CloudEventId = TryReadString(row, "CloudEventId"),
            CloudEventSource = TryReadString(row, "CloudEventSource"),
            CloudEventType = TryReadString(row, "CloudEventType"),
            CloudEventSubject = TryReadString(row, "CloudEventSubject"),
            DeadLetterReason = row.DeadLetterReason ?? string.Empty,
            DeadLetterErrorDescription = row.DeadLetterErrorDescription ?? string.Empty,
            MessageContent = JsonConvert.DeserializeObject<MessageContent>((string)row.MessageContentJson) ?? new MessageContent(),
        };
    }

    // ───────── Audit trail ─────────

    public async Task StoreMessageAudit(string eventId, MessageAuditEntity auditEntity, string? endpointId = null, string? eventTypeId = null)
    {
        var sql = $@"
INSERT INTO {T("MessageAudits")} (
    EventId, EndpointId, EventTypeId, AuditorName, AuditTimestamp, AuditType, Comment, AccessDenied, Data,
    CloudEventId, CloudEventSource, CloudEventType, CloudEventSubject)
VALUES (
    @EventId, @EndpointId, @EventTypeId, @AuditorName, @AuditTimestamp, @AuditType, @Comment, @AccessDenied, @Data,
    @CloudEventId, @CloudEventSource, @CloudEventType, @CloudEventSubject)";
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync(sql, new
        {
            EventId = eventId,
            EndpointId = endpointId,
            EventTypeId = eventTypeId,
            auditEntity.AuditorName,
            auditEntity.AuditTimestamp,
            AuditType = auditEntity.AuditType.ToString(),
            auditEntity.Comment,
            auditEntity.AccessDenied,
            auditEntity.Data,
            auditEntity.CloudEventId,
            auditEntity.CloudEventSource,
            auditEntity.CloudEventType,
            auditEntity.CloudEventSubject,
        }, commandTimeout: _commandTimeout);
    }

    public async Task<IEnumerable<MessageAuditEntity>> GetMessageAudits(string eventId)
    {
        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(
            $"SELECT * FROM {T("MessageAudits")} WHERE EventId = @EventId ORDER BY AuditTimestamp",
            new { EventId = eventId }, commandTimeout: _commandTimeout);
        return rows.Select(r => new MessageAuditEntity
        {
            AuditorName = r.AuditorName,
            AuditTimestamp = r.AuditTimestamp,
            AuditType = Enum.TryParse((string)r.AuditType, out MessageAuditType at) ? at : MessageAuditType.Comment,
            Comment = r.Comment,
            AccessDenied = r.AccessDenied is bool b ? b : false,
            Data = r.Data,
            EventId = r.EventId,
            EndpointId = r.EndpointId,
            CloudEventId = TryReadString(r, "CloudEventId"),
            CloudEventSource = TryReadString(r, "CloudEventSource"),
            CloudEventType = TryReadString(r, "CloudEventType"),
            CloudEventSubject = TryReadString(r, "CloudEventSubject"),
        }).ToList();
    }

    public async Task<AuditSearchResult> SearchAudits(AuditFilter filter, string? continuationToken, int maxItemCount)
    {
        var offset = DecodeOffset(continuationToken);
        var pageSize = PaginationLimits.Resolve(maxItemCount);

        var where = new List<string> { "1 = 1" };
        var p = new DynamicParameters();

        // Prefix matching on ID-like fields — see SearchMessages for the
        // cross-provider semantics and collation note.
        if (!string.IsNullOrEmpty(filter.EventId)) { where.Add(@"EventId LIKE @EventId ESCAPE '\'"); p.Add("EventId", LikePrefix(filter.EventId)); }
        if (!string.IsNullOrEmpty(filter.EndpointId))
        {
            // Exact scope (authorization-sensitive callers) vs. the historical
            // prefix match — see AuditFilter.EndpointIdExact. The comparison is
            // pinned to a fixed case-insensitive, accent-sensitive collation so
            // the semantics don't drift with the deployment's database collation
            // (a CS database would miss authorized case variants; a linguistic
            // AI collation could equate identifiers authorization treats as
            // distinct). The explicit COLLATE costs an index seek on this
            // predicate — acceptable for the endpoint-scoped audit page sizes.
            if (filter.EndpointIdExact) { where.Add("EndpointId COLLATE Latin1_General_100_CI_AS = @EndpointId"); p.Add("EndpointId", filter.EndpointId); }
            else { where.Add(@"EndpointId LIKE @EndpointId ESCAPE '\'"); p.Add("EndpointId", LikePrefix(filter.EndpointId)); }
        }
        if (!string.IsNullOrEmpty(filter.AuditorName)) { where.Add(@"AuditorName LIKE @AuditorName ESCAPE '\'"); p.Add("AuditorName", LikePrefix(filter.AuditorName)); }
        if (!string.IsNullOrEmpty(filter.EventTypeId)) { where.Add(@"EventTypeId LIKE @EventTypeId ESCAPE '\'"); p.Add("EventTypeId", LikePrefix(filter.EventTypeId)); }
        if (filter.AuditType.HasValue) { where.Add("AuditType = @AuditType"); p.Add("AuditType", filter.AuditType.Value.ToString()); }
        if (filter.CreatedAtFrom.HasValue) { where.Add("CreatedAtUtc >= @CreatedAtFrom"); p.Add("CreatedAtFrom", filter.CreatedAtFrom.Value); }
        if (filter.CreatedAtTo.HasValue) { where.Add("CreatedAtUtc <= @CreatedAtTo"); p.Add("CreatedAtTo", filter.CreatedAtTo.Value); }

        p.Add("Offset", offset);
        p.Add("PageSize", pageSize);

        var sql = $@"
SELECT EventId, EndpointId, EventTypeId, AuditorName, AuditTimestamp, AuditType, Comment, AccessDenied, Data,
       CloudEventId, CloudEventSource, CloudEventType, CloudEventSubject, CreatedAtUtc
FROM {T("MessageAudits")}
WHERE {string.Join(" AND ", where)}
ORDER BY CreatedAtUtc DESC, Id DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(sql, p, commandTimeout: _commandTimeout);

        var items = rows.Select(r => new AuditSearchItem
        {
            EventId = r.EventId,
            EndpointId = r.EndpointId,
            EventTypeId = r.EventTypeId,
            CreatedAt = r.CreatedAtUtc,
            Audit = new MessageAuditEntity
            {
                AuditorName = r.AuditorName,
                AuditTimestamp = r.AuditTimestamp,
                AuditType = Enum.TryParse((string)r.AuditType, out MessageAuditType at) ? at : MessageAuditType.Comment,
                Comment = r.Comment,
                AccessDenied = r.AccessDenied is bool b ? b : false,
                Data = r.Data,
                EventId = r.EventId,
                EndpointId = r.EndpointId,
                CloudEventId = TryReadString(r, "CloudEventId"),
                CloudEventSource = TryReadString(r, "CloudEventSource"),
                CloudEventType = TryReadString(r, "CloudEventType"),
                CloudEventSubject = TryReadString(r, "CloudEventSubject"),
            },
        }).ToList();

        return new AuditSearchResult
        {
            Audits = items,
            ContinuationToken = items.Count == pageSize ? EncodeOffset(offset + pageSize) : null,
        };
    }

    public async Task SetEventReport(string endpointId, string eventId, bool isReported, string? reportedBy, string? ticketId)
    {
        if (string.IsNullOrEmpty(endpointId)) throw new ArgumentNullException(nameof(endpointId));
        if (string.IsNullOrEmpty(eventId)) throw new ArgumentNullException(nameof(eventId));

        // HOLDLOCK (serializable range lock) closes the classic MERGE upsert
        // race: without it two concurrent first writes for the same key can both
        // miss the MATCHED branch and collide on the primary key.
        var sql = $@"
MERGE {T("EventReports")} WITH (HOLDLOCK) AS target
USING (SELECT @EndpointId AS EndpointId, @EventId AS EventId) AS src
ON target.EndpointId = src.EndpointId AND target.EventId = src.EventId
WHEN MATCHED THEN
    UPDATE SET IsReported = @IsReported, ReportedBy = @ReportedBy, ReportedAtUtc = @ReportedAtUtc, TicketId = @TicketId
WHEN NOT MATCHED THEN
    INSERT (EndpointId, EventId, IsReported, ReportedBy, ReportedAtUtc, TicketId)
    VALUES (@EndpointId, @EventId, @IsReported, @ReportedBy, @ReportedAtUtc, @TicketId);";

        await using var conn = await OpenAsync();
        await conn.ExecuteAsync(sql, new
        {
            EndpointId = endpointId,
            EventId = eventId,
            IsReported = isReported,
            ReportedBy = reportedBy,
            ReportedAtUtc = DateTime.UtcNow,
            // Clearing the marker drops the ticket reference too.
            TicketId = isReported ? ticketId : null,
        }, commandTimeout: _commandTimeout);
    }

    public async Task<IReadOnlyDictionary<string, EventReport>> GetEventReports(string endpointId, IReadOnlyCollection<string> eventIds)
    {
        var ids = (eventIds ?? Array.Empty<string>())
            .Where(e => !string.IsNullOrEmpty(e))
            .Distinct()
            .ToList();

        var result = new Dictionary<string, EventReport>();
        if (string.IsNullOrEmpty(endpointId) || ids.Count == 0)
            return result;

        var sql = $@"
SELECT EndpointId, EventId, IsReported, ReportedBy, ReportedAtUtc, TicketId
FROM {T("EventReports")}
WHERE EndpointId = @EndpointId AND EventId IN @EventIds";

        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync<EventReport>(sql,
            new { EndpointId = endpointId, EventIds = ids },
            commandTimeout: _commandTimeout);
        foreach (var r in rows)
        {
            if (!string.IsNullOrEmpty(r.EventId))
                result[r.EventId] = r;
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<string, int>> GetResubmitCounts(string endpointId, IReadOnlyCollection<string> eventIds)
    {
        var ids = (eventIds ?? Array.Empty<string>())
            .Where(e => !string.IsNullOrEmpty(e))
            .Distinct()
            .ToList();

        var result = new Dictionary<string, int>();
        if (string.IsNullOrEmpty(endpointId) || ids.Count == 0)
            return result;

        // AuditType is persisted as the enum *name* (see StoreMessageAudit), so
        // match on the string names. AccessDenied = 0 excludes denied resubmit
        // attempts (the WebApp logs those audit rows before returning
        // Unauthorized) — they never resubmitted.
        var sql = $@"
SELECT EventId, COUNT(*) AS Cnt
FROM {T("MessageAudits")}
WHERE EndpointId = @EndpointId
  AND AuditType IN @AuditTypes
  AND EventId IN @EventIds
  AND AccessDenied = 0
GROUP BY EventId";

        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(sql, new
        {
            EndpointId = endpointId,
            AuditTypes = new[]
            {
                nameof(MessageAuditType.Resubmit),
                nameof(MessageAuditType.ResubmitWithChanges),
            },
            EventIds = ids,
        }, commandTimeout: _commandTimeout);

        foreach (var row in rows)
        {
            string eventId = (string)row.EventId;
            if (!string.IsNullOrEmpty(eventId))
                result[eventId] = Convert.ToInt32(row.Cnt);
        }

        return result;
    }

    // ───────── State counts ─────────

    public async Task<EndpointStateCount> DownloadEndpointStateCount(string endpointId)
    {
        var sql = $@"
SELECT Status, COUNT(*) AS Count
FROM {T("UnresolvedEvents")}
WHERE EndpointId = @EndpointId AND Deleted = 0
GROUP BY Status";
        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync<(string Status, int Count)>(sql, new { EndpointId = endpointId }, commandTimeout: _commandTimeout);
        var dict = rows.ToDictionary(r => r.Status, r => r.Count);
        return new EndpointStateCount
        {
            EndpointId = endpointId,
            EventTime = DateTime.UtcNow,
            PendingCount = dict.GetValueOrDefault("Pending"),
            DeferredCount = dict.GetValueOrDefault("Deferred"),
            FailedCount = dict.GetValueOrDefault("Failed"),
            DeadletterCount = dict.GetValueOrDefault("DeadLettered"),
            UnsupportedCount = dict.GetValueOrDefault("Unsupported"),
        };
    }

    public async Task<SessionStateCount> DownloadEndpointSessionStateCount(string endpointId, string sessionId)
    {
        var sql = $@"
SELECT EventId, SessionId, Status
FROM {T("UnresolvedEvents")}
WHERE EndpointId = @EndpointId AND SessionId = @SessionId
  AND Status IN ('Pending','Deferred') AND Deleted = 0
ORDER BY UpdatedAtUtc DESC, Id DESC";
        await using var conn = await OpenAsync();
        var rows = (await conn.QueryAsync<(string EventId, string? SessionId, string Status)>(
            sql,
            new { EndpointId = endpointId, SessionId = sessionId },
            commandTimeout: _commandTimeout)).ToList();

        return new SessionStateCount
        {
            SessionId = sessionId,
            PendingEvents = rows.Where(r => r.Status == "Pending").Select(CompositeEventId),
            DeferredEvents = rows.Where(r => r.Status == "Deferred").Select(CompositeEventId),
        };
    }

    public async Task<IEnumerable<SessionStateCount>> DownloadEndpointSessionStateCountBatch(string endpointId, IEnumerable<string> sessionIds)
    {
        var ids = sessionIds.ToArray();
        if (ids.Length == 0) return Array.Empty<SessionStateCount>();
        var sql = $@"
SELECT EventId, SessionId, Status
FROM {T("UnresolvedEvents")}
WHERE EndpointId = @EndpointId AND SessionId IN @Ids
  AND Status IN ('Pending','Deferred') AND Deleted = 0
ORDER BY SessionId, UpdatedAtUtc DESC, Id DESC";
        await using var conn = await OpenAsync();
        var rows = (await conn.QueryAsync<(string EventId, string? SessionId, string Status)>(
            sql,
            new { EndpointId = endpointId, Ids = ids },
            commandTimeout: _commandTimeout)).ToList();

        var grouped = rows
            .Where(r => !string.IsNullOrEmpty(r.SessionId))
            .GroupBy(r => r.SessionId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        return ids.Select(sessionId =>
        {
            grouped.TryGetValue(sessionId, out var sessionRows);
            sessionRows ??= new List<(string EventId, string? SessionId, string Status)>();
            return new SessionStateCount
            {
                SessionId = sessionId,
                PendingEvents = sessionRows.Where(r => r.Status == "Pending").Select(CompositeEventId),
                DeferredEvents = sessionRows.Where(r => r.Status == "Deferred").Select(CompositeEventId),
            };
        }).ToList();
    }

    public async Task<EndpointState> DownloadEndpointStatePaging(string endpointId, int pageSize, string continuationToken)
    {
        var offset = DecodeOffset(continuationToken);
        var effectivePageSize = pageSize > 0 ? pageSize : 100;

        var sql = $@"
SELECT *
FROM {T("UnresolvedEvents")}
WHERE EndpointId = @EndpointId
  AND Status IN ('Pending','Deferred','Failed','DeadLettered','Unsupported')
  AND Deleted = 0
ORDER BY UpdatedAtUtc DESC, Id DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

        await using var conn = await OpenAsync();
        var rows = (await conn.QueryAsync(
            sql,
            new { EndpointId = endpointId, Offset = offset, PageSize = effectivePageSize },
            commandTimeout: _commandTimeout)).ToList();

        var events = rows.Select(MapUnresolvedEventRow).ToList();
        return new EndpointState
        {
            EndpointId = endpointId,
            EventTime = DateTime.UtcNow,
            EnrichedUnresolvedEvents = events,
            PendingEvents = events.Where(e => e.ResolutionStatus == ResolutionStatus.Pending).Select(CompositeEventId).ToList(),
            DeferredEvents = events.Where(e => e.ResolutionStatus == ResolutionStatus.Deferred).Select(CompositeEventId).ToList(),
            FailedEvents = events.Where(e => e.ResolutionStatus == ResolutionStatus.Failed).Select(CompositeEventId).ToList(),
            DeadletteredEvents = events.Where(e => e.ResolutionStatus == ResolutionStatus.DeadLettered).Select(CompositeEventId).ToList(),
            UnsupportedEvents = events.Where(e => e.ResolutionStatus == ResolutionStatus.Unsupported).Select(CompositeEventId).ToList(),
            ContinuationToken = events.Count == effectivePageSize ? EncodeOffset(offset + effectivePageSize) : string.Empty,
        };
    }

    // ───────── Single-event lookups ─────────

    public Task<UnresolvedEvent> GetPendingEvent(string endpointId, string eventId, string sessionId)
        => GetEventByStatus(endpointId, eventId, sessionId, "Pending");

    public Task<UnresolvedEvent> GetFailedEvent(string endpointId, string eventId, string sessionId)
        => GetEventByStatus(endpointId, eventId, sessionId, "Failed");

    public Task<UnresolvedEvent> GetDeferredEvent(string endpointId, string eventId, string sessionId)
        => GetEventByStatus(endpointId, eventId, sessionId, "Deferred");

    public Task<UnresolvedEvent> GetDeadletteredEvent(string endpointId, string eventId, string sessionId)
        => GetEventByStatus(endpointId, eventId, sessionId, "DeadLettered");

    public Task<UnresolvedEvent> GetUnsupportedEvent(string endpointId, string eventId, string sessionId)
        => GetEventByStatus(endpointId, eventId, sessionId, "Unsupported");

    private async Task<UnresolvedEvent> GetEventByStatus(string endpointId, string eventId, string sessionId, string status)
    {
        await using var conn = await OpenAsync();
        var row = await conn.QueryFirstOrDefaultAsync(
            $"SELECT * FROM {T("UnresolvedEvents")} WHERE EndpointId = @E AND EventId = @V AND SessionId = @S AND Status = @St AND Deleted = 0",
            new { E = endpointId, V = eventId, S = sessionId, St = status }, commandTimeout: _commandTimeout);
        return row == null ? throw new EndpointNotFoundException(endpointId) : MapUnresolvedEventRow(row);
    }

    public async Task<UnresolvedEvent> GetEvent(string endpointId, string eventId)
    {
        await using var conn = await OpenAsync();
        var row = await conn.QueryFirstOrDefaultAsync(
            $"SELECT TOP 1 * FROM {T("UnresolvedEvents")} WHERE EndpointId = @E AND EventId = @V AND Deleted = 0 ORDER BY UpdatedAtUtc DESC",
            new { E = endpointId, V = eventId }, commandTimeout: _commandTimeout);
        return row == null ? throw new EndpointNotFoundException(endpointId) : MapUnresolvedEventRow(row);
    }

    public async Task<UnresolvedEvent> GetPendingHandoffByExternalJobId(string endpointId, string externalJobId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(externalJobId)) return null;
        await using var conn = await OpenAsync();
        // Restrict to the pending-handoff slice so the filtered index in
        // 0011_HandoffLookup.sql is hit and we don't return stale failed/completed
        // rows where ExternalJobId may linger.
        var row = await conn.QueryFirstOrDefaultAsync(
            $@"SELECT TOP 1 * FROM {T("UnresolvedEvents")}
               WHERE EndpointId = @E
                 AND ExternalJobId = @X
                 AND PendingSubStatus = 'Handoff'
                 AND Status = 'Pending'
                 AND Deleted = 0
               ORDER BY UpdatedAtUtc DESC",
            new { E = endpointId, X = externalJobId }, commandTimeout: _commandTimeout);
        return row == null ? null : MapUnresolvedEventRow(row);
    }

    // Cap the event-type filter so a caller can't blow the parameter budget; agents subscribe to a
    // handful of types, well under this.
    private const int MaxEventTypeFilter = 64;

    public async Task<UnresolvedEvent?> GetNextPendingHandoffEvent(string endpointId, IReadOnlyCollection<string>? eventTypeIds)
    {
        await using var conn = await OpenAsync();
        var types = eventTypeIds?.Where(t => !string.IsNullOrEmpty(t)).Take(MaxEventTypeFilter).ToArray();
        var p = new DynamicParameters();
        p.Add("E", endpointId);
        // Bound to TOP 1 and filter status/sub-status/event-type server-side so the agent receive
        // long-poll no longer streams every pending row. Oldest-first (EnqueuedTimeUtc) gives FIFO.
        var sql = $@"SELECT TOP 1 * FROM {T("UnresolvedEvents")}
                     WHERE EndpointId = @E
                       AND PendingSubStatus = 'Handoff'
                       AND Status = 'Pending'
                       AND Deleted = 0";
        if (types is { Length: > 0 })
        {
            sql += " AND EventTypeId IN @Types";
            p.Add("Types", types);
        }
        sql += " ORDER BY EnqueuedTimeUtc ASC";

        var row = await conn.QueryFirstOrDefaultAsync(sql, p, commandTimeout: _commandTimeout);
        return row == null ? null : MapUnresolvedEventRow(row);
    }

    public Task<UnresolvedEvent> GetEventById(string endpointId, string id)
        => GetEvent(endpointId, id);

    public async Task<List<UnresolvedEvent>> GetEventsByIds(string endpointId, IEnumerable<string> eventIds)
    {
        var ids = eventIds.ToArray();
        if (ids.Length == 0) return new List<UnresolvedEvent>();
        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(
            $@"SELECT * FROM {T("UnresolvedEvents")}
               WHERE EndpointId = @E
                 AND (EventId IN @Ids OR CONCAT(EventId, '_', ISNULL(SessionId, '')) IN @Ids)
                 AND Deleted = 0",
            new { E = endpointId, Ids = ids }, commandTimeout: _commandTimeout);
        return rows.Select(MapUnresolvedEventRow).ToList();
    }

    public async Task<IEnumerable<UnresolvedEvent>> GetCompletedEventsOnEndpoint(string endpointId)
    {
        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(
            $"SELECT * FROM {T("UnresolvedEvents")} WHERE EndpointId = @E AND Status = 'Completed' AND Deleted = 0",
            new { E = endpointId }, commandTimeout: _commandTimeout);
        return rows.Select(MapUnresolvedEventRow).ToList();
    }

    public async Task<SearchResponse> GetEventsByFilter(EventFilter filter, string continuationToken, int maxSearchItemsCount)
    {
        var offset = DecodeOffset(continuationToken);
        var pageSize = PaginationLimits.Resolve(maxSearchItemsCount);

        var where = new List<string> { "Deleted = 0" };
        var p = new DynamicParameters();

        // Prefix matching on ID-like fields — see SearchMessages for the
        // cross-provider semantics and collation note.
        if (!string.IsNullOrEmpty(filter.EndPointId)) { where.Add(@"EndpointId LIKE @EndpointId ESCAPE '\'"); p.Add("EndpointId", LikePrefix(filter.EndPointId)); }
        if (!string.IsNullOrEmpty(filter.EventId)) { where.Add(@"EventId LIKE @EventId ESCAPE '\'"); p.Add("EventId", LikePrefix(filter.EventId)); }
        if (!string.IsNullOrEmpty(filter.SessionId)) { where.Add(@"SessionId LIKE @SessionId ESCAPE '\'"); p.Add("SessionId", LikePrefix(filter.SessionId)); }
        if (!string.IsNullOrEmpty(filter.To)) { where.Add("ToAddress = @ToAddress"); p.Add("ToAddress", filter.To); }
        if (!string.IsNullOrEmpty(filter.From)) { where.Add("FromAddress = @FromAddress"); p.Add("FromAddress", filter.From); }
        if (filter.UpdatedAtFrom.HasValue) { where.Add("UpdatedAtUtc >= @UpdatedAtFrom"); p.Add("UpdatedAtFrom", filter.UpdatedAtFrom.Value); }
        if (filter.UpdatedAtTo.HasValue) { where.Add("UpdatedAtUtc <= @UpdatedAtTo"); p.Add("UpdatedAtTo", filter.UpdatedAtTo.Value); }
        if (filter.EnqueuedAtFrom.HasValue) { where.Add("EnqueuedTimeUtc >= @EnqueuedAtFrom"); p.Add("EnqueuedAtFrom", filter.EnqueuedAtFrom.Value); }
        if (filter.EnqueuedAtTo.HasValue) { where.Add("EnqueuedTimeUtc <= @EnqueuedAtTo"); p.Add("EnqueuedAtTo", filter.EnqueuedAtTo.Value); }
        if (filter.MessageType.HasValue) { where.Add("MessageType = @MessageType"); p.Add("MessageType", filter.MessageType.Value.ToString()); }
        if (filter.EventTypeId is { Count: > 0 }) { where.Add("EventTypeId IN @EventTypeIds"); p.Add("EventTypeIds", filter.EventTypeId); }
        if (filter.ResolutionStatus is { Count: > 0 }) { where.Add("Status IN @Statuses"); p.Add("Statuses", filter.ResolutionStatus); }
        if (!string.IsNullOrEmpty(filter.Payload)) { where.Add(@"MessageContentJson LIKE @Payload ESCAPE '\'"); p.Add("Payload", "%" + LikePrefix(filter.Payload)); }

        p.Add("Offset", offset);
        p.Add("PageSize", pageSize);

        // Search results never surface the full request payload (cross-provider
        // contract — the detail view fetches it on demand). Strip the heavy
        // NVARCHAR(MAX) EventJson server-side so it never crosses the wire.
        var sql = $@"
SELECT
    EventId, SessionId, EndpointId, Status, UpdatedAtUtc, EnqueuedTimeUtc, CorrelationId, EndpointRole,
    MessageType, RetryCount, RetryLimit, LastMessageId, OriginatingMessageId, ParentMessageId,
    OriginatingFrom, Reason, DeadLetterReason, DeadLetterErrorDescription, EventTypeId,
    ToAddress, FromAddress, QueueTimeMs, ProcessingTimeMs,
    CloudEventId, CloudEventSource, CloudEventType, CloudEventSubject,
    PendingSubStatus, HandoffReason, ExternalJobId, ExpectedBy,
    JSON_MODIFY(MessageContentJson, '$.EventContent.EventJson', NULL) AS MessageContentJson
FROM {T("UnresolvedEvents")}
WHERE {string.Join(" AND ", where)}
ORDER BY UpdatedAtUtc DESC, Id DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(sql, p, commandTimeout: _commandTimeout);
        var events = rows.Select(MapUnresolvedEventRow).ToList();

        return new SearchResponse
        {
            Events = events,
            ContinuationToken = events.Count == pageSize ? EncodeOffset(offset + pageSize) : null!,
        };
    }

    /// <summary>
    /// Escapes LIKE wildcards (<c>\ % _ [</c>) in a user-supplied value and
    /// appends <c>%</c>, producing a safe prefix pattern for
    /// <c>LIKE @p ESCAPE '\'</c> filters. Prepend <c>%</c> to the result for a
    /// contains pattern.
    /// </summary>
    private static string LikePrefix(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal);
        return escaped + "%";
    }

    private static int DecodeOffset(string? token)
    {
        if (string.IsNullOrEmpty(token)) return 0;
        try { return int.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token!))); }
        catch { return 0; }
    }

    private static string EncodeOffset(int offset)
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(offset.ToString()));

    public async Task<IEnumerable<UnresolvedEvent>> GetPendingEventsOnSession(string endpointId)
    {
        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(
            $"SELECT * FROM {T("UnresolvedEvents")} WHERE EndpointId = @E AND Status = 'Pending' AND Deleted = 0",
            new { E = endpointId }, commandTimeout: _commandTimeout);
        return rows.Select(MapUnresolvedEventRow).ToList();
    }

    public Task<BlockedMessageEventPage> GetBlockedEventsOnSession(string endpointId, string sessionId, int skip, int take)
        => GetBlockedEventsOnSessionCore(endpointId, sessionId, skip, take);

    public Task<IEnumerable<BlockedMessageEvent>> GetInvalidEventsOnSession(string endpointId)
        => GetInvalidEventsOnSessionCore(endpointId);

    private async Task<BlockedMessageEventPage> GetBlockedEventsOnSessionCore(string endpointId, string sessionId, int skip, int take)
    {
        var safeSkip = skip < 0 ? 0 : skip;
        var safeTake = PaginationLimits.Resolve(take);

        await using var conn = await OpenAsync();
        using var multi = await conn.QueryMultipleAsync(
            $@"SELECT EventId, LastMessageId, OriginatingMessageId, Status
               FROM {T("UnresolvedEvents")}
               WHERE EndpointId = @EndpointId
                 AND SessionId = @SessionId
                 AND Status IN ('Pending','Deferred')
                 AND Deleted = 0
               ORDER BY UpdatedAtUtc DESC, Id DESC
               OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;

               SELECT COUNT(*)
               FROM {T("UnresolvedEvents")}
               WHERE EndpointId = @EndpointId
                 AND SessionId = @SessionId
                 AND Status IN ('Pending','Deferred')
                 AND Deleted = 0;",
            new { EndpointId = endpointId, SessionId = sessionId, Skip = safeSkip, Take = safeTake },
            commandTimeout: _commandTimeout);

        var rows = (await SqlServerExceptionTranslation.TranslateAsync(
            () => multi.ReadAsync())).ToList();
        var total = await SqlServerExceptionTranslation.TranslateAsync(
            () => multi.ReadFirstAsync<int>());

        return new BlockedMessageEventPage
        {
            Items = rows.Select(MapBlockedMessageEvent).Cast<BlockedMessageEvent>().ToList(),
            Total = total,
        };
    }

    private async Task<IEnumerable<BlockedMessageEvent>> GetInvalidEventsOnSessionCore(string endpointId)
    {
        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(
            $@"SELECT EventId, LastMessageId, OriginatingMessageId, Status
               FROM {T("UnresolvedEvents")}
               WHERE EndpointId = @EndpointId
                 AND EndpointRole = 'Publisher'
                 AND Deleted = 0
               ORDER BY UpdatedAtUtc DESC, Id DESC",
            new { EndpointId = endpointId },
            commandTimeout: _commandTimeout);

        return rows.Select(MapBlockedMessageEvent).Cast<BlockedMessageEvent>().ToList();
    }

    private static BlockedMessageEvent MapBlockedMessageEvent(dynamic row)
    {
        return new BlockedMessageEvent
        {
            EventId = row.EventId,
            OriginatingId = BlockedEventRules.ResolveOriginatingId((string?)row.OriginatingMessageId, (string?)row.LastMessageId),
            Status = row.Status,
        };
    }

    private static UnresolvedEvent MapUnresolvedEventRow(dynamic row)
    {
        return new UnresolvedEvent
        {
            EventId = row.EventId,
            SessionId = row.SessionId ?? string.Empty,
            EndpointId = row.EndpointId,
            ResolutionStatus = Enum.TryParse((string)row.Status, out ResolutionStatus rs) ? rs : ResolutionStatus.Pending,
            UpdatedAt = row.UpdatedAtUtc,
            EnqueuedTimeUtc = row.EnqueuedTimeUtc,
            CorrelationId = row.CorrelationId ?? string.Empty,
            EndpointRole = Enum.TryParse((string?)row.EndpointRole, out EndpointRole er) ? er : EndpointRole.Subscriber,
            MessageType = Enum.TryParse((string?)row.MessageType, out MessageType mt) ? mt : MessageType.EventRequest,
            RetryCount = row.RetryCount,
            RetryLimit = row.RetryLimit,
            LastMessageId = row.LastMessageId ?? string.Empty,
            OriginatingMessageId = row.OriginatingMessageId ?? string.Empty,
            ParentMessageId = row.ParentMessageId ?? string.Empty,
            OriginatingFrom = row.OriginatingFrom ?? string.Empty,
            Reason = row.Reason ?? string.Empty,
            DeadLetterReason = row.DeadLetterReason ?? string.Empty,
            DeadLetterErrorDescription = row.DeadLetterErrorDescription ?? string.Empty,
            EventTypeId = row.EventTypeId ?? string.Empty,
            To = row.ToAddress ?? string.Empty,
            From = row.FromAddress ?? string.Empty,
            QueueTimeMs = row.QueueTimeMs,
            ProcessingTimeMs = row.ProcessingTimeMs,
            CloudEventId = TryReadString(row, "CloudEventId"),
            CloudEventSource = TryReadString(row, "CloudEventSource"),
            CloudEventType = TryReadString(row, "CloudEventType"),
            CloudEventSubject = TryReadString(row, "CloudEventSubject"),
            PendingSubStatus = TryReadString(row, "PendingSubStatus"),
            HandoffReason = TryReadString(row, "HandoffReason"),
            ExternalJobId = TryReadString(row, "ExternalJobId"),
            ExpectedBy = TryReadDateTime(row, "ExpectedBy"),
            MessageContent = string.IsNullOrEmpty((string?)row.MessageContentJson)
                ? new MessageContent()
                : JsonConvert.DeserializeObject<MessageContent>((string)row.MessageContentJson) ?? new MessageContent(),
        };
    }

    // Dapper exposes rows as DapperRow, which is dictionary-like. Reading a column that
    // does not exist throws — guard with the dictionary view so old rows / older callers
    // don't break when the new nullable columns aren't projected.
    private static string TryReadString(dynamic row, string columnName)
    {
        var dict = (IDictionary<string, object>)row;
        return dict.TryGetValue(columnName, out var value) ? value as string : null;
    }

    private static DateTime? TryReadDateTime(dynamic row, string columnName)
    {
        var dict = (IDictionary<string, object>)row;
        if (!dict.TryGetValue(columnName, out var value) || value is null) return null;
        return value is DateTime dt ? dt : (DateTime?)null;
    }

    // ───────── Lifecycle / cleanup ─────────

    public async Task<bool> RemoveMessage(string eventId, string sessionId, string endpointId)
    {
        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(
            $"UPDATE {T("UnresolvedEvents")} SET Deleted = 1 WHERE EndpointId = @E AND EventId = @V AND SessionId = @S",
            new { E = endpointId, V = eventId, S = sessionId }, commandTimeout: _commandTimeout);
        return rows > 0;
    }

    public async Task<bool> PurgeMessages(string endpointId, string sessionId)
    {
        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(
            $"UPDATE {T("UnresolvedEvents")} SET Deleted = 1 WHERE EndpointId = @E AND SessionId = @S",
            new { E = endpointId, S = sessionId }, commandTimeout: _commandTimeout);
        return rows > 0;
    }

    public async Task<bool> PurgeMessages(string endpointId)
    {
        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(
            $"UPDATE {T("UnresolvedEvents")} SET Deleted = 1 WHERE EndpointId = @E",
            new { E = endpointId }, commandTimeout: _commandTimeout);
        return rows > 0;
    }

    public async Task ArchiveFailedEvent(string eventId, string sessionId, string endpointId)
    {
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync(
            $"UPDATE {T("UnresolvedEvents")} SET Deleted = 1 WHERE EndpointId = @E AND EventId = @V AND SessionId = @S",
            new { E = endpointId, V = eventId, S = sessionId }, commandTimeout: _commandTimeout);
    }

    public async Task<MessageSearchResult> SearchMessages(MessageFilter filter, string? continuationToken, int maxItemCount)
    {
        var offset = DecodeOffset(continuationToken);
        var pageSize = PaginationLimits.Resolve(maxItemCount);

        var where = new List<string> { "1 = 1" };
        var p = new DynamicParameters();

        // ID-like fields use PREFIX matching (LIKE 'value%') to converge with the
        // Cosmos provider's STARTSWITH semantics. Case-insensitivity relies on the
        // column collation being case-insensitive (the SQL Server default and what
        // the schema scripts assume).
        if (!string.IsNullOrEmpty(filter.EndpointId)) { where.Add(@"EndpointId LIKE @EndpointId ESCAPE '\'"); p.Add("EndpointId", LikePrefix(filter.EndpointId)); }
        if (!string.IsNullOrEmpty(filter.EventId)) { where.Add(@"EventId LIKE @EventId ESCAPE '\'"); p.Add("EventId", LikePrefix(filter.EventId)); }
        if (!string.IsNullOrEmpty(filter.MessageId)) { where.Add(@"MessageId LIKE @MessageId ESCAPE '\'"); p.Add("MessageId", LikePrefix(filter.MessageId)); }
        if (!string.IsNullOrEmpty(filter.SessionId)) { where.Add(@"SessionId LIKE @SessionId ESCAPE '\'"); p.Add("SessionId", LikePrefix(filter.SessionId)); }
        if (!string.IsNullOrEmpty(filter.From)) { where.Add("FromAddress = @FromAddress"); p.Add("FromAddress", filter.From); }
        if (!string.IsNullOrEmpty(filter.To)) { where.Add("ToAddress = @ToAddress"); p.Add("ToAddress", filter.To); }
        if (filter.MessageType.HasValue) { where.Add("MessageType = @MessageType"); p.Add("MessageType", filter.MessageType.Value.ToString()); }
        if (filter.EnqueuedAtFrom.HasValue) { where.Add("EnqueuedTimeUtc >= @EnqueuedAtFrom"); p.Add("EnqueuedAtFrom", filter.EnqueuedAtFrom.Value); }
        if (filter.EnqueuedAtTo.HasValue) { where.Add("EnqueuedTimeUtc <= @EnqueuedAtTo"); p.Add("EnqueuedAtTo", filter.EnqueuedAtTo.Value); }
        if (filter.EventTypeId is { Count: > 0 }) { where.Add("EventTypeId IN @EventTypeIds"); p.Add("EventTypeIds", filter.EventTypeId); }

        p.Add("Offset", offset);
        p.Add("PageSize", pageSize);

        // Search results never surface the full request payload (cross-provider
        // contract — detail views fetch it via GetMessage). Strip the heavy
        // NVARCHAR(MAX) EventJson server-side so it never crosses the wire.
        var sql = $@"
SELECT
    EventId, MessageId, EndpointId, SessionId, CorrelationId, EventTypeId,
    OriginatingMessageId, ParentMessageId, FromAddress, ToAddress, OriginatingFrom, OriginalSessionId,
    MessageType, EndpointRole, EnqueuedTimeUtc, RetryCount, RetryLimit, DeferralSequence,
    QueueTimeMs, ProcessingTimeMs, CloudEventId, CloudEventSource, CloudEventType, CloudEventSubject,
    DeadLetterReason, DeadLetterErrorDescription,
    JSON_MODIFY(MessageContentJson, '$.EventContent.EventJson', NULL) AS MessageContentJson
FROM {T("Messages")}
WHERE {string.Join(" AND ", where)}
ORDER BY EnqueuedTimeUtc DESC, Id DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(sql, p, commandTimeout: _commandTimeout);
        var messages = rows.Select(r => (MessageEntity)MapMessageRow(r)).ToList();

        return new MessageSearchResult
        {
            Messages = messages,
            ContinuationToken = messages.Count == pageSize ? EncodeOffset(offset + pageSize) : null,
        };
    }

    public Task<string> GetEndpointErrorList(string endpointId)
        => GetEndpointErrorListCore(endpointId);

    private async Task<string> GetEndpointErrorListCore(string endpointId)
    {
        await using var conn = await OpenAsync();
        var ids = await conn.QueryAsync<string>(
            $@"SELECT CONCAT(EventId, '_', ISNULL(SessionId, ''))
               FROM {T("UnresolvedEvents")}
               WHERE EndpointId = @EndpointId
                 AND Status IN (@FailedStatus, @DeferredStatus)
                 AND Deleted = 0
               ORDER BY UpdatedAtUtc DESC, Id DESC",
            new
            {
                EndpointId = endpointId,
                EndpointErrorListFormat.FailedStatus,
                EndpointErrorListFormat.DeferredStatus,
            },
            commandTimeout: _commandTimeout);

        return EndpointErrorListFormat.Format(ids.ToList());
    }

    // ───────── Subscription store ─────────

    public async Task<EndpointSubscription> SubscribeToEndpointNotification(string endpointId, string mail, string type, string author, string url, List<string> eventTypes, string payload, int frequency)
    {
        var sub = new EndpointSubscription
        {
            Id = Guid.NewGuid().ToString(),
            EndpointId = endpointId,
            Mail = mail,
            Type = type,
            AuthorId = author,
            Url = url,
            EventTypes = eventTypes,
            Payload = payload,
            Frequency = frequency,
        };
        var sql = $@"
INSERT INTO {T("EndpointSubscriptions")} (Id, EndpointId, Type, Mail, AuthorId, Url, EventTypesJson, Payload, Frequency)
VALUES (@Id, @EndpointId, @Type, @Mail, @AuthorId, @Url, @EventTypesJson, @Payload, @Frequency)";
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync(sql, new
        {
            sub.Id, sub.EndpointId, sub.Type, sub.Mail, sub.AuthorId, sub.Url,
            EventTypesJson = JsonConvert.SerializeObject(eventTypes),
            sub.Payload, sub.Frequency
        }, commandTimeout: _commandTimeout);
        return sub;
    }

    public async Task<IEnumerable<EndpointSubscription>> GetSubscriptionsOnEndpoint(string endpointId)
    {
        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(
            $"SELECT * FROM {T("EndpointSubscriptions")} WHERE EndpointId = @E",
            new { E = endpointId }, commandTimeout: _commandTimeout);
        return rows.Select(MapSubscriptionRow).ToList();
    }

    public Task<IEnumerable<EndpointSubscription>> GetSubscriptionsOnEndpointWithEventtype(string endpoint, string eventtypes, string payload, string errorText)
        => GetSubscriptionsOnEndpoint(endpoint);

    public async Task<bool> UpdateSubscription(EndpointSubscription subscription)
    {
        var sql = $@"
UPDATE {T("EndpointSubscriptions")}
SET Mail = @Mail, Type = @Type, Url = @Url, EventTypesJson = @EventTypesJson, Payload = @Payload, Frequency = @Frequency
WHERE Id = @Id";
        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(sql, new
        {
            subscription.Id, subscription.Mail, subscription.Type, subscription.Url,
            EventTypesJson = JsonConvert.SerializeObject(subscription.EventTypes),
            subscription.Payload, subscription.Frequency
        }, commandTimeout: _commandTimeout);
        return rows > 0;
    }

    public async Task<bool> UnsubscribeById(string endpointId, string id)
    {
        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(
            $"DELETE FROM {T("EndpointSubscriptions")} WHERE Id = @Id AND EndpointId = @E",
            new { Id = id, E = endpointId }, commandTimeout: _commandTimeout);
        return rows > 0;
    }

    public async Task<bool> UnsubscribeByMail(string endpointId, string mail)
    {
        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(
            $"DELETE FROM {T("EndpointSubscriptions")} WHERE Mail = @Mail AND EndpointId = @E",
            new { Mail = mail, E = endpointId }, commandTimeout: _commandTimeout);
        return rows > 0;
    }

    public async Task<bool> DeleteSubscription(string subscriptionId)
    {
        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(
            $"DELETE FROM {T("EndpointSubscriptions")} WHERE Id = @Id",
            new { Id = subscriptionId }, commandTimeout: _commandTimeout);
        return rows > 0;
    }

    private static EndpointSubscription MapSubscriptionRow(dynamic row) => new()
    {
        Id = row.Id,
        EndpointId = row.EndpointId,
        Type = row.Type ?? string.Empty,
        NotificationSeverity = row.NotificationSeverity ?? string.Empty,
        Mail = row.Mail ?? string.Empty,
        AuthorId = row.AuthorId ?? string.Empty,
        NotifiedAt = row.NotifiedAt ?? string.Empty,
        ErrorList = row.ErrorList ?? string.Empty,
        Url = row.Url ?? string.Empty,
        EventTypes = string.IsNullOrEmpty((string?)row.EventTypesJson)
            ? new List<string>()
            : JsonConvert.DeserializeObject<List<string>>((string)row.EventTypesJson) ?? new List<string>(),
        Payload = row.Payload ?? string.Empty,
        Frequency = row.Frequency,
    };

    // ───────── Endpoint metadata ─────────

    public async Task<EndpointMetadata> GetEndpointMetadata(string endpointId)
    {
        await using var conn = await OpenAsync();
        var row = await conn.QueryFirstOrDefaultAsync(
            $"SELECT * FROM {T("EndpointMetadata")} WHERE EndpointId = @E",
            new { E = endpointId }, commandTimeout: _commandTimeout);
        if (row == null) throw new EndpointNotFoundException(endpointId);
        var metadata = MapMetadataRow(row);
        metadata.Heartbeats = await GetHeartbeats(conn, endpointId);
        return metadata;
    }

    public async Task<List<EndpointMetadata>> GetMetadatas()
    {
        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync($"SELECT * FROM {T("EndpointMetadata")}", commandTimeout: _commandTimeout);
        return rows.Select(MapMetadataRow).Cast<EndpointMetadata>().ToList();
    }

    public async Task<List<EndpointMetadata>?> GetMetadatas(IEnumerable<string> endpointIds)
    {
        var ids = endpointIds.ToArray();
        if (ids.Length == 0) return new List<EndpointMetadata>();
        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(
            $"SELECT * FROM {T("EndpointMetadata")} WHERE EndpointId IN @Ids",
            new { Ids = ids }, commandTimeout: _commandTimeout);
        return rows.Select(MapMetadataRow).Cast<EndpointMetadata>().ToList();
    }

    public async Task<bool> SetEndpointMetadata(EndpointMetadata endpointMetadata)
    {
        var sql = $@"
MERGE {T("EndpointMetadata")} AS target
USING (SELECT @EndpointId AS EndpointId) AS source
ON target.EndpointId = source.EndpointId
WHEN MATCHED THEN UPDATE SET
    EndpointOwner = @EndpointOwner,
    EndpointOwnerTeam = @EndpointOwnerTeam,
    EndpointOwnerEmail = @EndpointOwnerEmail,
    IsHeartbeatEnabled = @IsHeartbeatEnabled,
    EndpointHeartbeatStatus = @Status,
    TechnicalContactsJson = @TechnicalContactsJson,
    SubscriptionStatus = @SubscriptionStatus,
    UpdatedAtUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (
    EndpointId, EndpointOwner, EndpointOwnerTeam, EndpointOwnerEmail,
    IsHeartbeatEnabled, EndpointHeartbeatStatus, TechnicalContactsJson, SubscriptionStatus)
VALUES (@EndpointId, @EndpointOwner, @EndpointOwnerTeam, @EndpointOwnerEmail,
    @IsHeartbeatEnabled, @Status, @TechnicalContactsJson, @SubscriptionStatus);";
        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(sql, new
        {
            endpointMetadata.EndpointId,
            endpointMetadata.EndpointOwner,
            endpointMetadata.EndpointOwnerTeam,
            endpointMetadata.EndpointOwnerEmail,
            endpointMetadata.IsHeartbeatEnabled,
            Status = endpointMetadata.EndpointHeartbeatStatus?.ToString(),
            TechnicalContactsJson = JsonConvert.SerializeObject(endpointMetadata.TechnicalContacts ?? new List<TechnicalContact>()),
            endpointMetadata.SubscriptionStatus,
        }, commandTimeout: _commandTimeout);
        return rows > 0;
    }

    private static EndpointMetadata MapMetadataRow(dynamic row) => new()
    {
        EndpointId = row.EndpointId,
        EndpointOwner = row.EndpointOwner ?? string.Empty,
        EndpointOwnerTeam = row.EndpointOwnerTeam ?? string.Empty,
        EndpointOwnerEmail = row.EndpointOwnerEmail ?? string.Empty,
        IsHeartbeatEnabled = row.IsHeartbeatEnabled,
        EndpointHeartbeatStatus = Enum.TryParse((string?)row.EndpointHeartbeatStatus, out HeartbeatStatus rollup)
            ? rollup
            : (HeartbeatStatus?)null,
        TechnicalContacts = string.IsNullOrEmpty((string?)row.TechnicalContactsJson)
            ? new List<TechnicalContact>()
            : JsonConvert.DeserializeObject<List<TechnicalContact>>((string)row.TechnicalContactsJson) ?? new List<TechnicalContact>(),
        Heartbeats = new List<Heartbeat>(),
        SubscriptionStatus = row.SubscriptionStatus,
    };

    // ───────── Endpoint heartbeat ─────────

    public async Task<List<EndpointMetadata>> GetMetadatasWithEnabledHeartbeat()
    {
        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(
            $"SELECT * FROM {T("EndpointMetadata")} WHERE IsHeartbeatEnabled = 1",
            commandTimeout: _commandTimeout);
        return rows.Select(MapMetadataRow).Cast<EndpointMetadata>().ToList();
    }

    public async Task EnableHeartbeatOnEndpoint(string endpointId, bool enable)
    {
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync(
            $@"MERGE {T("EndpointMetadata")} AS target
               USING (SELECT @EndpointId AS EndpointId) AS source ON target.EndpointId = source.EndpointId
               WHEN MATCHED THEN UPDATE SET IsHeartbeatEnabled = @Enable, UpdatedAtUtc = SYSUTCDATETIME()
               WHEN NOT MATCHED THEN INSERT (EndpointId, IsHeartbeatEnabled) VALUES (@EndpointId, @Enable);",
            new { EndpointId = endpointId, Enable = enable }, commandTimeout: _commandTimeout);
    }

    public async Task<bool> SetHeartbeat(Heartbeat heartbeat, string endpointId)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);

        // MERGE on (EndpointId, MessageId): the Pending probe and the answer that
        // settles it share one row instead of accumulating a duplicate.
        var sql = $@"
MERGE {T("Heartbeats")} AS target
USING (SELECT @EndpointId AS EndpointId, @MessageId AS MessageId) AS source
ON target.EndpointId = source.EndpointId
   AND ((target.MessageId = source.MessageId) OR (target.MessageId IS NULL AND source.MessageId IS NULL))
WHEN MATCHED THEN UPDATE SET
    StartTimeUtc = @StartTime,
    ReceivedTimeUtc = @ReceivedTime,
    EndTimeUtc = @EndTime,
    EndpointHeartbeatStatus = @Status,
    SdkVersion = @SdkVersion,
    IntervalSeconds = CASE WHEN @IntervalSeconds > 0 THEN @IntervalSeconds ELSE target.IntervalSeconds END
WHEN NOT MATCHED THEN INSERT (EndpointId, MessageId, StartTimeUtc, ReceivedTimeUtc, EndTimeUtc, EndpointHeartbeatStatus, SdkVersion, IntervalSeconds)
VALUES (@EndpointId, @MessageId, @StartTime, @ReceivedTime, @EndTime, @Status, @SdkVersion, @IntervalSeconds);

WITH ranked AS (
    SELECT Id,
           ROW_NUMBER() OVER (PARTITION BY EndpointId ORDER BY StartTimeUtc DESC, Id DESC) AS rn
    FROM {T("Heartbeats")}
    WHERE EndpointId = @EndpointId
)
DELETE FROM ranked
WHERE rn > @MaxHeartbeats;

MERGE {T("EndpointMetadata")} AS target
USING (SELECT @EndpointId AS EndpointId) AS source
ON target.EndpointId = source.EndpointId
WHEN NOT MATCHED THEN INSERT (EndpointId, EndpointHeartbeatStatus)
VALUES (@EndpointId, @Status);

{RollupSql("m.EndpointId = @EndpointId")}";
        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(sql, new
        {
            EndpointId = endpointId,
            heartbeat.MessageId,
            heartbeat.StartTime,
            heartbeat.ReceivedTime,
            heartbeat.EndTime,
            Status = heartbeat.EndpointHeartbeatStatus.ToString(),
            heartbeat.SdkVersion,
            heartbeat.IntervalSeconds,
            MaxHeartbeats = HeartbeatRollup.MaxHeartbeatsPerEndpoint,
            PendingStatus = nameof(HeartbeatStatus.Pending),
        }, commandTimeout: _commandTimeout);
        return rows > 0;
    }

    /// <summary>
    /// Rollup mirrors the most recent settled probe (On/Off/Unsupported); an
    /// in-flight Pending must not mask the last known outcome. Pending only
    /// before the first settled result. Expects @PendingStatus as a parameter.
    /// </summary>
    private string RollupSql(string endpointFilter) => $@"
UPDATE m
SET EndpointHeartbeatStatus = COALESCE(
        settled.EndpointHeartbeatStatus,
        CASE WHEN EXISTS (SELECT 1 FROM {T("Heartbeats")} h WHERE h.EndpointId = m.EndpointId)
             THEN @PendingStatus END,
        m.EndpointHeartbeatStatus),
    UpdatedAtUtc = SYSUTCDATETIME()
FROM {T("EndpointMetadata")} m
OUTER APPLY (
    SELECT TOP 1 h.EndpointHeartbeatStatus
    FROM {T("Heartbeats")} h
    WHERE h.EndpointId = m.EndpointId
      AND h.EndpointHeartbeatStatus <> @PendingStatus
    ORDER BY h.StartTimeUtc DESC, h.Id DESC
) settled
WHERE {endpointFilter};";

    public async Task<List<string>> SweepTimedOutHeartbeats(DateTime cutoffUtc)
    {
        var sql = $@"
DECLARE @swept TABLE (EndpointId NVARCHAR(200));

UPDATE {T("Heartbeats")}
SET EndpointHeartbeatStatus = @OffStatus
OUTPUT inserted.EndpointId INTO @swept
WHERE EndpointHeartbeatStatus = @PendingStatus
  AND StartTimeUtc <= @Cutoff;

{RollupSql("m.EndpointId IN (SELECT DISTINCT EndpointId FROM @swept)")}

SELECT DISTINCT EndpointId FROM @swept;";
        await using var conn = await OpenAsync();
        var swept = await conn.QueryAsync<string>(sql, new
        {
            Cutoff = cutoffUtc,
            OffStatus = nameof(HeartbeatStatus.Off),
            PendingStatus = nameof(HeartbeatStatus.Pending),
        }, commandTimeout: _commandTimeout);
        return swept.ToList();
    }

    public async Task<HeartbeatSettings> GetHeartbeatSettings()
    {
        await using var conn = await OpenAsync();
        var row = await conn.QueryFirstOrDefaultAsync(
            $@"SELECT TOP 1 Id, Enabled, IntervalSeconds, TimeoutSeconds, LastSentAtUtc, LastHeartbeatFoldAtUtc
               FROM {T("HeartbeatSettings")}
               WHERE Id = @Id",
            new { Id = HeartbeatSettings.SingletonId },
            commandTimeout: _commandTimeout);

        return row == null
            ? new HeartbeatSettings()
            : new HeartbeatSettings
            {
                Id = row.Id,
                Enabled = row.Enabled,
                IntervalSeconds = row.IntervalSeconds,
                TimeoutSeconds = row.TimeoutSeconds,
                LastSentAtUtc = row.LastSentAtUtc,
                LastHeartbeatFoldAtUtc = row.LastHeartbeatFoldAtUtc,
            };
    }

    public async Task<bool> SetHeartbeatSettings(HeartbeatSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.Id)) settings.Id = HeartbeatSettings.SingletonId;

        // COALESCE on LastSentAtUtc: the claim owns that field, so an operator edit
        // that carries no value must not reset the send schedule.
        var sql = $@"
MERGE {T("HeartbeatSettings")} AS target
USING (SELECT @Id AS Id) AS source
ON target.Id = source.Id
WHEN MATCHED THEN UPDATE SET
    Enabled = @Enabled,
    IntervalSeconds = @IntervalSeconds,
    TimeoutSeconds = @TimeoutSeconds,
    LastSentAtUtc = COALESCE(@LastSentAtUtc, target.LastSentAtUtc),
    LastHeartbeatFoldAtUtc = COALESCE(@LastHeartbeatFoldAtUtc, target.LastHeartbeatFoldAtUtc),
    UpdatedAtUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (Id, Enabled, IntervalSeconds, TimeoutSeconds, LastSentAtUtc, LastHeartbeatFoldAtUtc)
VALUES (@Id, @Enabled, @IntervalSeconds, @TimeoutSeconds, @LastSentAtUtc, @LastHeartbeatFoldAtUtc);";
        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(sql, settings, commandTimeout: _commandTimeout);
        return rows > 0;
    }

    public async Task<bool> TryClaimHeartbeatSend(DateTime dueBefore)
    {
        // The rows-affected check is what makes at most one scaled-out instance
        // send per interval.
        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(
            $@"UPDATE {T("HeartbeatSettings")}
               SET LastSentAtUtc = SYSUTCDATETIME(),
                   UpdatedAtUtc = SYSUTCDATETIME()
               WHERE Id = @Id
                 AND Enabled = 1
                 AND (LastSentAtUtc IS NULL OR LastSentAtUtc <= @DueBefore)",
            new { Id = HeartbeatSettings.SingletonId, DueBefore = dueBefore },
            commandTimeout: _commandTimeout);
        return rows == 1;
    }

    public async Task<List<HeartbeatOverviewItem>> GetHeartbeatOverview()
    {
        // Status = last settled outcome (On/Off/Unsupported); an in-flight Pending
        // probe never masks it. Response fields (round-trip, last seen, SDK version)
        // come from the last actual response — a swept/timed-out row carried none.
        // Mirrors HeartbeatRollup.BuildOverviewItem, which the document-shaped
        // providers use.
        var sql = $@"
WITH latest AS (
    SELECT EndpointId,
           MessageId,
           StartTimeUtc,
           ROW_NUMBER() OVER (PARTITION BY EndpointId ORDER BY StartTimeUtc DESC, Id DESC) AS rn
    FROM {T("Heartbeats")}
),
settled AS (
    SELECT EndpointId,
           EndpointHeartbeatStatus,
           ROW_NUMBER() OVER (PARTITION BY EndpointId ORDER BY StartTimeUtc DESC, Id DESC) AS rn
    FROM {T("Heartbeats")}
    WHERE EndpointHeartbeatStatus <> @PendingStatus
),
responded AS (
    SELECT EndpointId,
           StartTimeUtc,
           ReceivedTimeUtc,
           EndTimeUtc,
           SdkVersion,
           ROW_NUMBER() OVER (PARTITION BY EndpointId ORDER BY StartTimeUtc DESC, Id DESC) AS rn
    FROM {T("Heartbeats")}
    WHERE EndpointHeartbeatStatus IN (@OnStatus, @UnsupportedStatus)
)
SELECT m.EndpointId,
       m.IsHeartbeatEnabled,
       l.MessageId,
       l.StartTimeUtc AS LastStartTime,
       r.ReceivedTimeUtc AS LastReceivedTime,
       r.EndTimeUtc AS LastEndTime,
       CASE
           WHEN r.StartTimeUtc IS NULL OR r.EndTimeUtc IS NULL THEN NULL
           ELSE DATEDIFF_BIG(millisecond, r.StartTimeUtc, r.EndTimeUtc)
       END AS RoundTripMs,
       r.SdkVersion,
       COALESCE(s.EndpointHeartbeatStatus,
                CASE WHEN l.EndpointId IS NOT NULL THEN @PendingStatus END,
                m.EndpointHeartbeatStatus,
                @UnknownStatus) AS Status
FROM {T("EndpointMetadata")} AS m
LEFT JOIN latest AS l
    ON l.EndpointId = m.EndpointId AND l.rn = 1
LEFT JOIN settled AS s
    ON s.EndpointId = m.EndpointId AND s.rn = 1
LEFT JOIN responded AS r
    ON r.EndpointId = m.EndpointId AND r.rn = 1
ORDER BY m.EndpointId";

        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(sql, new
        {
            PendingStatus = nameof(HeartbeatStatus.Pending),
            OnStatus = nameof(HeartbeatStatus.On),
            UnsupportedStatus = nameof(HeartbeatStatus.Unsupported),
            UnknownStatus = nameof(HeartbeatStatus.Unknown),
        }, commandTimeout: _commandTimeout);
        return rows.Select(row => new HeartbeatOverviewItem
        {
            EndpointId = row.EndpointId,
            IsHeartbeatEnabled = row.IsHeartbeatEnabled,
            MessageId = row.MessageId ?? string.Empty,
            LastStartTime = row.LastStartTime,
            LastReceivedTime = row.LastReceivedTime,
            LastEndTime = row.LastEndTime,
            RoundTripMs = row.RoundTripMs == null ? null : (long?)row.RoundTripMs,
            SdkVersion = row.SdkVersion ?? string.Empty,
            Status = Enum.TryParse((string?)row.Status, out HeartbeatStatus status)
                ? status
                : HeartbeatStatus.Unknown,
        }).Cast<HeartbeatOverviewItem>().ToList();
    }

    private async Task<List<Heartbeat>> GetHeartbeats(SqlConnection conn, string endpointId)
    {
        var rows = await conn.QueryAsync(
            $@"SELECT MessageId, StartTimeUtc, ReceivedTimeUtc, EndTimeUtc, EndpointHeartbeatStatus, SdkVersion, IntervalSeconds
               FROM {T("Heartbeats")}
               WHERE EndpointId = @EndpointId
               ORDER BY StartTimeUtc",
            new { EndpointId = endpointId },
            commandTimeout: _commandTimeout);

        return rows.Select(row => new Heartbeat
        {
            MessageId = row.MessageId ?? string.Empty,
            StartTime = row.StartTimeUtc,
            ReceivedTime = row.ReceivedTimeUtc,
            EndTime = row.EndTimeUtc,
            SdkVersion = row.SdkVersion ?? string.Empty,
            IntervalSeconds = row.IntervalSeconds,
            EndpointHeartbeatStatus = Enum.TryParse((string?)row.EndpointHeartbeatStatus, out HeartbeatStatus status)
                ? status
                : HeartbeatStatus.Unknown,
        }).Cast<Heartbeat>().ToList();
    }

    // ───────── Durable endpoint heartbeat history ─────────

    public async Task<List<HeartbeatUptimeDay>> GetHeartbeatUptimeDays(DateTime fromDayUtc)
    {
        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync<HeartbeatUptimeDay>(
            $@"SELECT EndpointId, DayUtc, Expected, Received, Missed, ObservedSeconds,
                      LongestGapSeconds, LastBeatUtc
               FROM {T("HeartbeatUptimeDays")}
               WHERE DayUtc >= @FromDayUtc
               ORDER BY EndpointId, DayUtc",
            new { FromDayUtc = fromDayUtc.Date },
            commandTimeout: _commandTimeout);
        return rows.Select(day =>
        {
            day.Id = $"{day.EndpointId}|{day.DayUtc:yyyy-MM-dd}";
            return day;
        }).ToList();
    }

    public async Task<bool> UpsertHeartbeatUptimeDays(IEnumerable<HeartbeatUptimeDay> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        var rows = days.ToList();
        if (rows.Count == 0) return true;

        const string fields = "Expected = @Expected, Received = @Received, Missed = @Missed, "
            + "ObservedSeconds = @ObservedSeconds, LongestGapSeconds = @LongestGapSeconds, LastBeatUtc = @LastBeatUtc";
        var sql = $@"
MERGE {T("HeartbeatUptimeDays")} WITH (HOLDLOCK) AS target
USING (SELECT @EndpointId AS EndpointId, @DayUtc AS DayUtc) AS source
ON target.EndpointId = source.EndpointId AND target.DayUtc = source.DayUtc
WHEN MATCHED THEN UPDATE SET {fields}
WHEN NOT MATCHED THEN INSERT
    (EndpointId, DayUtc, Expected, Received, Missed, ObservedSeconds, LongestGapSeconds, LastBeatUtc)
VALUES
    (@EndpointId, @DayUtc, @Expected, @Received, @Missed, @ObservedSeconds, @LongestGapSeconds, @LastBeatUtc);";
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync(sql, rows, commandTimeout: _commandTimeout);
        return true;
    }

    public async Task<List<HeartbeatGap>> GetHeartbeatGaps(DateTime fromUtc)
    {
        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync<HeartbeatGap>(
            $@"SELECT EndpointId, FromUtc, ToUtc, SdkVersionBefore, SdkVersionAfter
               FROM {T("HeartbeatGaps")}
               WHERE ToUtc IS NULL OR ToUtc >= @FromUtc
               ORDER BY FromUtc DESC",
            new { FromUtc = fromUtc },
            commandTimeout: _commandTimeout);
        return rows.Select(gap =>
        {
            gap.Id = $"{gap.EndpointId}|{gap.FromUtc:O}";
            return gap;
        }).ToList();
    }

    public async Task<bool> UpsertHeartbeatGaps(IEnumerable<HeartbeatGap> gaps)
    {
        ArgumentNullException.ThrowIfNull(gaps);
        var rows = gaps.ToList();
        if (rows.Count == 0) return true;

        var sql = $@"
MERGE {T("HeartbeatGaps")} WITH (HOLDLOCK) AS target
USING (SELECT @EndpointId AS EndpointId, @FromUtc AS FromUtc) AS source
ON target.EndpointId = source.EndpointId AND target.FromUtc = source.FromUtc
WHEN MATCHED THEN UPDATE SET
    ToUtc = @ToUtc, SdkVersionBefore = @SdkVersionBefore, SdkVersionAfter = @SdkVersionAfter
WHEN NOT MATCHED THEN INSERT
    (EndpointId, FromUtc, ToUtc, SdkVersionBefore, SdkVersionAfter)
VALUES
    (@EndpointId, @FromUtc, @ToUtc, @SdkVersionBefore, @SdkVersionAfter);";
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync(sql, rows, commandTimeout: _commandTimeout);
        return true;
    }

    public async Task<bool> TryClaimHeartbeatHistoryFold(DateTime dueBefore)
    {
        var sql = $@"
MERGE {T("HeartbeatSettings")} WITH (HOLDLOCK) AS target
USING (SELECT @Id AS Id) AS source ON target.Id = source.Id
WHEN MATCHED AND (target.LastHeartbeatFoldAtUtc IS NULL OR target.LastHeartbeatFoldAtUtc <= @DueBefore)
    THEN UPDATE SET LastHeartbeatFoldAtUtc = SYSUTCDATETIME(), UpdatedAtUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (Id, Enabled, IntervalSeconds, TimeoutSeconds, LastHeartbeatFoldAtUtc)
    VALUES (@Id, 0, 300, 60, SYSUTCDATETIME());";
        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(
            sql,
            new { Id = HeartbeatSettings.SingletonId, DueBefore = dueBefore },
            commandTimeout: _commandTimeout);
        return rows == 1;
    }

    public async Task PruneHeartbeatHistory(DateTime cutoffUtc)
    {
        var sql = $@"
DELETE FROM {T("HeartbeatUptimeDays")} WHERE DayUtc < @CutoffDayUtc;
DELETE FROM {T("HeartbeatGaps")} WHERE ToUtc IS NOT NULL AND ToUtc < @CutoffUtc;";
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync(
            sql,
            new { CutoffDayUtc = cutoffUtc.Date, CutoffUtc = cutoffUtc },
            commandTimeout: _commandTimeout);
    }

    // ───────── Service health (platform services, not endpoints) ─────────

    public async Task<List<ServiceHealth>> GetServiceHealth()
    {
        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(
            $@"SELECT ServiceId, Status, Version, LastProbeMessageId, LastProbeSentUtc, LastSeenUtc, RoundTripMs
               FROM {T("ServiceHealth")}
               ORDER BY ServiceId",
            commandTimeout: _commandTimeout);

        return rows.Select(row => new ServiceHealth
        {
            ServiceId = row.ServiceId,
            Status = Enum.TryParse((string?)row.Status, out HeartbeatStatus status) ? status : HeartbeatStatus.Unknown,
            Version = row.Version ?? string.Empty,
            LastProbeMessageId = row.LastProbeMessageId,
            LastProbeSentUtc = row.LastProbeSentUtc,
            LastSeenUtc = row.LastSeenUtc,
            RoundTripMs = row.RoundTripMs == null ? null : (long?)row.RoundTripMs,
        }).Cast<ServiceHealth>().ToList();
    }

    public async Task<bool> TryClaimServiceProbe(string serviceId, DateTime dueBefore, string probeMessageId)
    {
        if (string.IsNullOrWhiteSpace(serviceId)) throw new ArgumentNullException(nameof(serviceId));

        // Single conditional statement: the rows-affected check is what makes at
        // most one scaled-out instance send per interval. The MERGE covers the
        // first probe for a service the seed migration did not create.
        var sql = $@"
MERGE {T("ServiceHealth")} AS target
USING (SELECT @ServiceId AS ServiceId) AS source
ON target.ServiceId = source.ServiceId
WHEN MATCHED AND (target.LastProbeSentUtc IS NULL OR target.LastProbeSentUtc <= @DueBefore) THEN UPDATE SET
    LastProbeSentUtc = SYSUTCDATETIME(),
    LastProbeMessageId = @ProbeMessageId,
    UpdatedAtUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (ServiceId, Status, LastProbeSentUtc, LastProbeMessageId)
VALUES (@ServiceId, @UnknownStatus, SYSUTCDATETIME(), @ProbeMessageId);";

        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(sql, new
        {
            ServiceId = serviceId,
            DueBefore = dueBefore,
            ProbeMessageId = probeMessageId,
            UnknownStatus = nameof(HeartbeatStatus.Unknown),
        }, commandTimeout: _commandTimeout);
        return rows == 1;
    }

    public async Task<bool> SetServiceHealth(ServiceHealth serviceHealth)
    {
        ArgumentNullException.ThrowIfNull(serviceHealth);
        if (string.IsNullOrWhiteSpace(serviceHealth.ServiceId)) throw new ArgumentNullException(nameof(serviceHealth));

        // LastProbeSentUtc is owned by the claim, so a response must not touch it —
        // otherwise an answer would reset the send schedule.
        var sql = $@"
MERGE {T("ServiceHealth")} AS target
USING (SELECT @ServiceId AS ServiceId) AS source
ON target.ServiceId = source.ServiceId
WHEN MATCHED THEN UPDATE SET
    Status = @Status,
    Version = @Version,
    LastProbeMessageId = NULL,
    LastSeenUtc = @LastSeenUtc,
    RoundTripMs = @RoundTripMs,
    UpdatedAtUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (ServiceId, Status, Version, LastSeenUtc, RoundTripMs)
VALUES (@ServiceId, @Status, @Version, @LastSeenUtc, @RoundTripMs);";

        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(sql, new
        {
            serviceHealth.ServiceId,
            Status = serviceHealth.Status.ToString(),
            serviceHealth.Version,
            serviceHealth.LastSeenUtc,
            serviceHealth.RoundTripMs,
        }, commandTimeout: _commandTimeout);
        return rows > 0;
    }

    public async Task<List<string>> SweepTimedOutServiceProbes(DateTime cutoffUtc)
    {
        var sql = $@"
DECLARE @swept TABLE (ServiceId NVARCHAR(100));

UPDATE {T("ServiceHealth")}
SET Status = @OffStatus,
    LastProbeMessageId = NULL,
    UpdatedAtUtc = SYSUTCDATETIME()
OUTPUT inserted.ServiceId INTO @swept
WHERE LastProbeMessageId IS NOT NULL
  AND LastProbeSentUtc IS NOT NULL
  AND LastProbeSentUtc <= @Cutoff;

SELECT ServiceId FROM @swept;";

        await using var conn = await OpenAsync();
        var swept = await conn.QueryAsync<string>(sql, new
        {
            Cutoff = cutoffUtc,
            OffStatus = nameof(HeartbeatStatus.Off),
        }, commandTimeout: _commandTimeout);
        return swept.ToList();
    }

    // ───────── Metrics — implementation in SqlServerMetricsStore ─────────

    public Task<EndpointMetricsResult> GetEndpointMetrics(DateTime from) => _metrics.GetEndpointMetrics(from);

    public Task<EndpointLatencyMetricsResult> GetEndpointLatencyMetrics(DateTime from) => _metrics.GetEndpointLatencyMetrics(from);

    public Task<List<FailedMessageInfo>> GetFailedMessageInsights(DateTime from) => _metrics.GetFailedMessageInsights(from);

    public Task<TimeSeriesResult> GetTimeSeriesMetrics(DateTime from, int substringLength, string bucketLabel) => _metrics.GetTimeSeriesMetrics(from, substringLength, bucketLabel);

    public Task<EventTypeTimeSeriesResult> GetEventTypeTimeSeriesMetrics(DateTime from, int substringLength, string bucketLabel) => _metrics.GetEventTypeTimeSeriesMetrics(from, substringLength, bucketLabel);

    private static string CompositeEventId((string EventId, string? SessionId, string Status) row)
        => $"{row.EventId}_{row.SessionId ?? string.Empty}";

    private static string CompositeEventId(UnresolvedEvent @event)
        => $"{@event.EventId}_{@event.SessionId ?? string.Empty}";

    // ───────── Event schema store — implementation in SqlServerEventSchemaStore ─────────

    public Task<EventSchema?> GetSchema(string eventTypeId) => _eventSchemas.GetSchema(eventTypeId);

    public Task<IReadOnlyList<EventSchema>> GetSchemas() => _eventSchemas.GetSchemas();

    public Task<EventSchema> DefineEventType(EventSchema schema) => _eventSchemas.DefineEventType(schema);

    // ───────── Access-control store (spec 026) — implementation in SqlServerAccessControlStore ─────────

    public Task<AccessControlList?> GetSiteAccessControl() => _accessControl.GetSiteAccessControl();

    public Task SetSiteAccessControl(AccessControlList accessControl) => _accessControl.SetSiteAccessControl(accessControl);

    public Task<AccessControlList?> GetEndpointAccessControl(string endpointId) => _accessControl.GetEndpointAccessControl(endpointId);

    public Task<IReadOnlyList<AccessControlList>> GetEndpointAccessControls() => _accessControl.GetEndpointAccessControls();

    public Task SetEndpointAccessControl(string endpointId, AccessControlList accessControl) => _accessControl.SetEndpointAccessControl(endpointId, accessControl);
}
