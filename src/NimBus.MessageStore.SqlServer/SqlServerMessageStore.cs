using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
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
public sealed class SqlServerMessageStore : INimBusMessageStore
{
    private readonly SqlServerMessageStoreOptions _options;
    private readonly string _schema;
    private readonly int _commandTimeout;

    public SqlServerMessageStore(IOptions<SqlServerMessageStoreOptions> options)
    {
        _options = options.Value;
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
    MessageContentJson = @MessageContentJson,
    Deleted = 0
WHEN NOT MATCHED THEN INSERT (
    EventId, SessionId, EndpointId, Status, UpdatedAtUtc, EnqueuedTimeUtc, CorrelationId, EndpointRole,
    MessageType, RetryCount, RetryLimit, LastMessageId, OriginatingMessageId, ParentMessageId,
    OriginatingFrom, Reason, DeadLetterReason, DeadLetterErrorDescription, EventTypeId,
    ToAddress, FromAddress, QueueTimeMs, ProcessingTimeMs, MessageContentJson)
VALUES (
    @EventId, @SessionId, @EndpointId, @Status, @UpdatedAt, @EnqueuedTimeUtc, @CorrelationId, @EndpointRole,
    @MessageType, @RetryCount, @RetryLimit, @LastMessageId, @OriginatingMessageId, @ParentMessageId,
    @OriginatingFrom, @Reason, @DeadLetterReason, @DeadLetterErrorDescription, @EventTypeId,
    @ToAddress, @FromAddress, @QueueTimeMs, @ProcessingTimeMs, @MessageContentJson);";

        await using var conn = Open();
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
    QueueTimeMs, ProcessingTimeMs, DeadLetterReason, DeadLetterErrorDescription, MessageContentJson)
VALUES (
    @EventId, @MessageId, @EndpointId, @SessionId, @CorrelationId, @EventTypeId,
    @OriginatingMessageId, @ParentMessageId, @FromAddress, @ToAddress, @OriginatingFrom, @OriginalSessionId,
    @MessageType, @EndpointRole, @EnqueuedTimeUtc, @RetryCount, @RetryLimit, @DeferralSequence,
    @QueueTimeMs, @ProcessingTimeMs, @DeadLetterReason, @DeadLetterErrorDescription, @MessageContentJson);";

        await using var conn = Open();
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
            message.DeadLetterReason,
            message.DeadLetterErrorDescription,
            MessageContentJson = JsonConvert.SerializeObject(message.MessageContent),
        }, commandTimeout: _commandTimeout);
    }

    public async Task<MessageEntity> GetMessage(string eventId, string messageId)
    {
        await using var conn = Open();
        var row = await conn.QueryFirstOrDefaultAsync(
            $"SELECT * FROM {T("Messages")} WHERE EventId = @EventId AND MessageId = @MessageId",
            new { EventId = eventId, MessageId = messageId }, commandTimeout: _commandTimeout);
        return row == null
            ? throw new MessageNotFoundException(eventId, messageId)
            : MapMessageRow(row);
    }

    public async Task<IEnumerable<MessageEntity>> GetEventHistory(string eventId)
    {
        await using var conn = Open();
        var rows = await conn.QueryAsync(
            $"SELECT * FROM {T("Messages")} WHERE EventId = @EventId ORDER BY EnqueuedTimeUtc",
            new { EventId = eventId }, commandTimeout: _commandTimeout);
        return rows.Select(MapMessageRow).ToList();
    }

    public async Task<MessageEntity> GetFailedMessage(string eventId, string endpointId)
    {
        await using var conn = Open();
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
        await using var conn = Open();
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
            DeadLetterReason = row.DeadLetterReason ?? string.Empty,
            DeadLetterErrorDescription = row.DeadLetterErrorDescription ?? string.Empty,
            MessageContent = JsonConvert.DeserializeObject<MessageContent>((string)row.MessageContentJson) ?? new MessageContent(),
        };
    }

    // ───────── Audit trail ─────────

    public async Task StoreMessageAudit(string eventId, MessageAuditEntity auditEntity, string? endpointId = null, string? eventTypeId = null)
    {
        var sql = $@"
INSERT INTO {T("MessageAudits")} (EventId, EndpointId, EventTypeId, AuditorName, AuditTimestamp, AuditType, Comment)
VALUES (@EventId, @EndpointId, @EventTypeId, @AuditorName, @AuditTimestamp, @AuditType, @Comment)";
        await using var conn = Open();
        await conn.ExecuteAsync(sql, new
        {
            EventId = eventId,
            EndpointId = endpointId,
            EventTypeId = eventTypeId,
            auditEntity.AuditorName,
            auditEntity.AuditTimestamp,
            AuditType = auditEntity.AuditType.ToString(),
            auditEntity.Comment,
        }, commandTimeout: _commandTimeout);
    }

    public async Task<IEnumerable<MessageAuditEntity>> GetMessageAudits(string eventId)
    {
        await using var conn = Open();
        var rows = await conn.QueryAsync(
            $"SELECT * FROM {T("MessageAudits")} WHERE EventId = @EventId ORDER BY AuditTimestamp",
            new { EventId = eventId }, commandTimeout: _commandTimeout);
        return rows.Select(r => new MessageAuditEntity
        {
            AuditorName = r.AuditorName,
            AuditTimestamp = r.AuditTimestamp,
            AuditType = Enum.TryParse((string)r.AuditType, out MessageAuditType at) ? at : MessageAuditType.Comment,
            Comment = r.Comment,
        }).ToList();
    }

    public async Task<AuditSearchResult> SearchAudits(AuditFilter filter, string? continuationToken, int maxItemCount)
    {
        var offset = DecodeOffset(continuationToken);
        var pageSize = maxItemCount > 0 ? maxItemCount : 100;

        var where = new List<string> { "1 = 1" };
        var p = new DynamicParameters();

        if (!string.IsNullOrEmpty(filter.EventId)) { where.Add("EventId = @EventId"); p.Add("EventId", filter.EventId); }
        if (!string.IsNullOrEmpty(filter.EndpointId)) { where.Add("EndpointId = @EndpointId"); p.Add("EndpointId", filter.EndpointId); }
        if (!string.IsNullOrEmpty(filter.AuditorName)) { where.Add("AuditorName = @AuditorName"); p.Add("AuditorName", filter.AuditorName); }
        if (!string.IsNullOrEmpty(filter.EventTypeId)) { where.Add("EventTypeId = @EventTypeId"); p.Add("EventTypeId", filter.EventTypeId); }
        if (filter.AuditType.HasValue) { where.Add("AuditType = @AuditType"); p.Add("AuditType", filter.AuditType.Value.ToString()); }
        if (filter.CreatedAtFrom.HasValue) { where.Add("CreatedAtUtc >= @CreatedAtFrom"); p.Add("CreatedAtFrom", filter.CreatedAtFrom.Value); }
        if (filter.CreatedAtTo.HasValue) { where.Add("CreatedAtUtc <= @CreatedAtTo"); p.Add("CreatedAtTo", filter.CreatedAtTo.Value); }

        p.Add("Offset", offset);
        p.Add("PageSize", pageSize);

        var sql = $@"
SELECT EventId, EndpointId, EventTypeId, AuditorName, AuditTimestamp, AuditType, Comment, CreatedAtUtc
FROM {T("MessageAudits")}
WHERE {string.Join(" AND ", where)}
ORDER BY CreatedAtUtc DESC, Id DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

        await using var conn = Open();
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
            },
        }).ToList();

        return new AuditSearchResult
        {
            Audits = items,
            ContinuationToken = items.Count == pageSize ? EncodeOffset(offset + pageSize) : null,
        };
    }

    // ───────── State counts ─────────

    public async Task<EndpointStateCount> DownloadEndpointStateCount(string endpointId)
    {
        var sql = $@"
SELECT Status, COUNT(*) AS Count
FROM {T("UnresolvedEvents")}
WHERE EndpointId = @EndpointId AND Deleted = 0
GROUP BY Status";
        await using var conn = Open();
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
SELECT COUNT(*) FROM {T("UnresolvedEvents")}
WHERE EndpointId = @EndpointId AND SessionId = @SessionId
  AND Status IN ('Pending','Deferred') AND Deleted = 0";
        await using var conn = Open();
        var count = await conn.ExecuteScalarAsync<int>(sql, new { EndpointId = endpointId, SessionId = sessionId }, commandTimeout: _commandTimeout);
        return new SessionStateCount { SessionId = sessionId, PendingEvents = Array.Empty<string>(), DeferredEvents = Array.Empty<string>() };
    }

    public async Task<IEnumerable<SessionStateCount>> DownloadEndpointSessionStateCountBatch(string endpointId, IEnumerable<string> sessionIds)
    {
        var ids = sessionIds.ToArray();
        if (ids.Length == 0) return Array.Empty<SessionStateCount>();
        var sql = $@"
SELECT SessionId, COUNT(*) AS Count
FROM {T("UnresolvedEvents")}
WHERE EndpointId = @EndpointId AND SessionId IN @Ids
  AND Status IN ('Pending','Deferred') AND Deleted = 0
GROUP BY SessionId";
        await using var conn = Open();
        var rows = await conn.QueryAsync<(string SessionId, int Count)>(sql, new { EndpointId = endpointId, Ids = ids }, commandTimeout: _commandTimeout);
        return rows.Select(r => new SessionStateCount { SessionId = r.SessionId, PendingEvents = Array.Empty<string>(), DeferredEvents = Array.Empty<string>() }).ToList();
    }

    public Task<EndpointState> DownloadEndpointStatePaging(string endpointId, int pageSize, string continuationToken)
        => Task.FromResult(new EndpointState { EndpointId = endpointId, EventTime = DateTime.UtcNow });

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
        await using var conn = Open();
        var row = await conn.QueryFirstOrDefaultAsync(
            $"SELECT * FROM {T("UnresolvedEvents")} WHERE EndpointId = @E AND EventId = @V AND SessionId = @S AND Status = @St AND Deleted = 0",
            new { E = endpointId, V = eventId, S = sessionId, St = status }, commandTimeout: _commandTimeout);
        return row == null ? throw new EndpointNotFoundException(endpointId) : MapUnresolvedEventRow(row);
    }

    public async Task<UnresolvedEvent> GetEvent(string endpointId, string eventId)
    {
        await using var conn = Open();
        var row = await conn.QueryFirstOrDefaultAsync(
            $"SELECT TOP 1 * FROM {T("UnresolvedEvents")} WHERE EndpointId = @E AND EventId = @V AND Deleted = 0 ORDER BY UpdatedAtUtc DESC",
            new { E = endpointId, V = eventId }, commandTimeout: _commandTimeout);
        return row == null ? throw new EndpointNotFoundException(endpointId) : MapUnresolvedEventRow(row);
    }

    public Task<UnresolvedEvent> GetEventById(string endpointId, string id)
        => GetEvent(endpointId, id);

    public async Task<List<UnresolvedEvent>> GetEventsByIds(string endpointId, IEnumerable<string> eventIds)
    {
        var ids = eventIds.ToArray();
        if (ids.Length == 0) return new List<UnresolvedEvent>();
        await using var conn = Open();
        var rows = await conn.QueryAsync(
            $"SELECT * FROM {T("UnresolvedEvents")} WHERE EndpointId = @E AND EventId IN @Ids AND Deleted = 0",
            new { E = endpointId, Ids = ids }, commandTimeout: _commandTimeout);
        return rows.Select(MapUnresolvedEventRow).ToList();
    }

    public async Task<IEnumerable<UnresolvedEvent>> GetCompletedEventsOnEndpoint(string endpointId)
    {
        await using var conn = Open();
        var rows = await conn.QueryAsync(
            $"SELECT * FROM {T("UnresolvedEvents")} WHERE EndpointId = @E AND Status = 'Completed' AND Deleted = 0",
            new { E = endpointId }, commandTimeout: _commandTimeout);
        return rows.Select(MapUnresolvedEventRow).ToList();
    }

    public async Task<SearchResponse> GetEventsByFilter(EventFilter filter, string continuationToken, int maxSearchItemsCount)
    {
        var offset = DecodeOffset(continuationToken);
        var pageSize = maxSearchItemsCount > 0 ? maxSearchItemsCount : 100;

        var where = new List<string> { "Deleted = 0" };
        var p = new DynamicParameters();

        if (!string.IsNullOrEmpty(filter.EndPointId)) { where.Add("EndpointId = @EndpointId"); p.Add("EndpointId", filter.EndPointId); }
        if (!string.IsNullOrEmpty(filter.EventId)) { where.Add("EventId = @EventId"); p.Add("EventId", filter.EventId); }
        if (!string.IsNullOrEmpty(filter.SessionId)) { where.Add("SessionId = @SessionId"); p.Add("SessionId", filter.SessionId); }
        if (!string.IsNullOrEmpty(filter.To)) { where.Add("ToAddress = @ToAddress"); p.Add("ToAddress", filter.To); }
        if (!string.IsNullOrEmpty(filter.From)) { where.Add("FromAddress = @FromAddress"); p.Add("FromAddress", filter.From); }
        if (filter.UpdatedAtFrom.HasValue) { where.Add("UpdatedAtUtc >= @UpdatedAtFrom"); p.Add("UpdatedAtFrom", filter.UpdatedAtFrom.Value); }
        if (filter.UpdatedAtTo.HasValue) { where.Add("UpdatedAtUtc <= @UpdatedAtTo"); p.Add("UpdatedAtTo", filter.UpdatedAtTo.Value); }
        if (filter.EnqueuedAtFrom.HasValue) { where.Add("EnqueuedTimeUtc >= @EnqueuedAtFrom"); p.Add("EnqueuedAtFrom", filter.EnqueuedAtFrom.Value); }
        if (filter.EnqueuedAtTo.HasValue) { where.Add("EnqueuedTimeUtc <= @EnqueuedAtTo"); p.Add("EnqueuedAtTo", filter.EnqueuedAtTo.Value); }
        if (filter.MessageType.HasValue) { where.Add("MessageType = @MessageType"); p.Add("MessageType", filter.MessageType.Value.ToString()); }
        if (filter.EventTypeId is { Count: > 0 }) { where.Add("EventTypeId IN @EventTypeIds"); p.Add("EventTypeIds", filter.EventTypeId); }
        if (filter.ResolutionStatus is { Count: > 0 }) { where.Add("Status IN @Statuses"); p.Add("Statuses", filter.ResolutionStatus); }
        if (!string.IsNullOrEmpty(filter.Payload)) { where.Add("MessageContentJson LIKE @Payload"); p.Add("Payload", $"%{filter.Payload}%"); }

        p.Add("Offset", offset);
        p.Add("PageSize", pageSize);

        var sql = $@"
SELECT * FROM {T("UnresolvedEvents")}
WHERE {string.Join(" AND ", where)}
ORDER BY UpdatedAtUtc DESC, Id DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

        await using var conn = Open();
        var rows = await conn.QueryAsync(sql, p, commandTimeout: _commandTimeout);
        var events = rows.Select(MapUnresolvedEventRow).ToList();

        return new SearchResponse
        {
            Events = events,
            ContinuationToken = events.Count == pageSize ? EncodeOffset(offset + pageSize) : null!,
        };
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
        await using var conn = Open();
        var rows = await conn.QueryAsync(
            $"SELECT * FROM {T("UnresolvedEvents")} WHERE EndpointId = @E AND Status = 'Pending' AND Deleted = 0",
            new { E = endpointId }, commandTimeout: _commandTimeout);
        return rows.Select(MapUnresolvedEventRow).ToList();
    }

    public Task<IEnumerable<BlockedMessageEvent>> GetBlockedEventsOnSession(string endpointId, string sessionId)
        => Task.FromResult<IEnumerable<BlockedMessageEvent>>(Array.Empty<BlockedMessageEvent>());

    public Task<IEnumerable<BlockedMessageEvent>> GetInvalidEventsOnSession(string endpointId)
        => Task.FromResult<IEnumerable<BlockedMessageEvent>>(Array.Empty<BlockedMessageEvent>());

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
            MessageContent = string.IsNullOrEmpty((string?)row.MessageContentJson)
                ? new MessageContent()
                : JsonConvert.DeserializeObject<MessageContent>((string)row.MessageContentJson) ?? new MessageContent(),
        };
    }

    // ───────── Lifecycle / cleanup ─────────

    public async Task<bool> RemoveMessage(string eventId, string sessionId, string endpointId)
    {
        await using var conn = Open();
        var rows = await conn.ExecuteAsync(
            $"UPDATE {T("UnresolvedEvents")} SET Deleted = 1 WHERE EndpointId = @E AND EventId = @V AND SessionId = @S",
            new { E = endpointId, V = eventId, S = sessionId }, commandTimeout: _commandTimeout);
        return rows > 0;
    }

    public async Task<bool> PurgeMessages(string endpointId, string sessionId)
    {
        await using var conn = Open();
        var rows = await conn.ExecuteAsync(
            $"UPDATE {T("UnresolvedEvents")} SET Deleted = 1 WHERE EndpointId = @E AND SessionId = @S",
            new { E = endpointId, S = sessionId }, commandTimeout: _commandTimeout);
        return rows > 0;
    }

    public async Task<bool> PurgeMessages(string endpointId)
    {
        await using var conn = Open();
        var rows = await conn.ExecuteAsync(
            $"UPDATE {T("UnresolvedEvents")} SET Deleted = 1 WHERE EndpointId = @E",
            new { E = endpointId }, commandTimeout: _commandTimeout);
        return rows > 0;
    }

    public async Task ArchiveFailedEvent(string eventId, string sessionId, string endpointId)
    {
        await using var conn = Open();
        await conn.ExecuteAsync(
            $"UPDATE {T("UnresolvedEvents")} SET Deleted = 1 WHERE EndpointId = @E AND EventId = @V AND SessionId = @S",
            new { E = endpointId, V = eventId, S = sessionId }, commandTimeout: _commandTimeout);
    }

    public async Task<MessageSearchResult> SearchMessages(MessageFilter filter, string? continuationToken, int maxItemCount)
    {
        var offset = DecodeOffset(continuationToken);
        var pageSize = maxItemCount > 0 ? maxItemCount : 100;

        var where = new List<string> { "1 = 1" };
        var p = new DynamicParameters();

        if (!string.IsNullOrEmpty(filter.EndpointId)) { where.Add("EndpointId = @EndpointId"); p.Add("EndpointId", filter.EndpointId); }
        if (!string.IsNullOrEmpty(filter.EventId)) { where.Add("EventId = @EventId"); p.Add("EventId", filter.EventId); }
        if (!string.IsNullOrEmpty(filter.MessageId)) { where.Add("MessageId = @MessageId"); p.Add("MessageId", filter.MessageId); }
        if (!string.IsNullOrEmpty(filter.SessionId)) { where.Add("SessionId = @SessionId"); p.Add("SessionId", filter.SessionId); }
        if (!string.IsNullOrEmpty(filter.From)) { where.Add("FromAddress = @FromAddress"); p.Add("FromAddress", filter.From); }
        if (!string.IsNullOrEmpty(filter.To)) { where.Add("ToAddress = @ToAddress"); p.Add("ToAddress", filter.To); }
        if (filter.MessageType.HasValue) { where.Add("MessageType = @MessageType"); p.Add("MessageType", filter.MessageType.Value.ToString()); }
        if (filter.EnqueuedAtFrom.HasValue) { where.Add("EnqueuedTimeUtc >= @EnqueuedAtFrom"); p.Add("EnqueuedAtFrom", filter.EnqueuedAtFrom.Value); }
        if (filter.EnqueuedAtTo.HasValue) { where.Add("EnqueuedTimeUtc <= @EnqueuedAtTo"); p.Add("EnqueuedAtTo", filter.EnqueuedAtTo.Value); }
        if (filter.EventTypeId is { Count: > 0 }) { where.Add("EventTypeId IN @EventTypeIds"); p.Add("EventTypeIds", filter.EventTypeId); }

        p.Add("Offset", offset);
        p.Add("PageSize", pageSize);

        var sql = $@"
SELECT * FROM {T("Messages")}
WHERE {string.Join(" AND ", where)}
ORDER BY EnqueuedTimeUtc DESC, Id DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

        await using var conn = Open();
        var rows = await conn.QueryAsync(sql, p, commandTimeout: _commandTimeout);
        var messages = rows.Select(r => (MessageEntity)MapMessageRow(r)).ToList();

        return new MessageSearchResult
        {
            Messages = messages,
            ContinuationToken = messages.Count == pageSize ? EncodeOffset(offset + pageSize) : null,
        };
    }

    public Task<string> GetEndpointErrorList(string endpointId)
        => Task.FromResult(string.Empty);

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
        await using var conn = Open();
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
        await using var conn = Open();
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
        await using var conn = Open();
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
        await using var conn = Open();
        var rows = await conn.ExecuteAsync(
            $"DELETE FROM {T("EndpointSubscriptions")} WHERE Id = @Id AND EndpointId = @E",
            new { Id = id, E = endpointId }, commandTimeout: _commandTimeout);
        return rows > 0;
    }

    public async Task<bool> UnsubscribeByMail(string endpointId, string mail)
    {
        await using var conn = Open();
        var rows = await conn.ExecuteAsync(
            $"DELETE FROM {T("EndpointSubscriptions")} WHERE Mail = @Mail AND EndpointId = @E",
            new { Mail = mail, E = endpointId }, commandTimeout: _commandTimeout);
        return rows > 0;
    }

    public async Task<bool> DeleteSubscription(string subscriptionId)
    {
        await using var conn = Open();
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

    // ───────── Endpoint metadata + heartbeat ─────────

    public async Task<EndpointMetadata> GetEndpointMetadata(string endpointId)
    {
        await using var conn = Open();
        var row = await conn.QueryFirstOrDefaultAsync(
            $"SELECT * FROM {T("EndpointMetadata")} WHERE EndpointId = @E",
            new { E = endpointId }, commandTimeout: _commandTimeout);
        if (row == null) throw new EndpointNotFoundException(endpointId);
        return MapMetadataRow(row);
    }

    public async Task<List<EndpointMetadata>> GetMetadatas()
    {
        await using var conn = Open();
        var rows = await conn.QueryAsync($"SELECT * FROM {T("EndpointMetadata")}", commandTimeout: _commandTimeout);
        return rows.Select(MapMetadataRow).Cast<EndpointMetadata>().ToList();
    }

    public async Task<List<EndpointMetadata>?> GetMetadatas(IEnumerable<string> endpointIds)
    {
        var ids = endpointIds.ToArray();
        if (ids.Length == 0) return new List<EndpointMetadata>();
        await using var conn = Open();
        var rows = await conn.QueryAsync(
            $"SELECT * FROM {T("EndpointMetadata")} WHERE EndpointId IN @Ids",
            new { Ids = ids }, commandTimeout: _commandTimeout);
        return rows.Select(MapMetadataRow).Cast<EndpointMetadata>().ToList();
    }

    public async Task<List<EndpointMetadata>> GetMetadatasWithEnabledHeartbeat()
    {
        await using var conn = Open();
        var rows = await conn.QueryAsync(
            $"SELECT * FROM {T("EndpointMetadata")} WHERE IsHeartbeatEnabled = 1",
            commandTimeout: _commandTimeout);
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
    EndpointId, EndpointOwner, EndpointOwnerTeam, EndpointOwnerEmail, IsHeartbeatEnabled,
    EndpointHeartbeatStatus, TechnicalContactsJson, SubscriptionStatus)
VALUES (@EndpointId, @EndpointOwner, @EndpointOwnerTeam, @EndpointOwnerEmail, @IsHeartbeatEnabled,
    @Status, @TechnicalContactsJson, @SubscriptionStatus);";
        await using var conn = Open();
        var rows = await conn.ExecuteAsync(sql, new
        {
            endpointMetadata.EndpointId,
            endpointMetadata.EndpointOwner,
            endpointMetadata.EndpointOwnerTeam,
            endpointMetadata.EndpointOwnerEmail,
            endpointMetadata.IsHeartbeatEnabled,
            Status = endpointMetadata.EndpointHeartbeatStatus.ToString(),
            TechnicalContactsJson = JsonConvert.SerializeObject(endpointMetadata.TechnicalContacts ?? new List<TechnicalContact>()),
            endpointMetadata.SubscriptionStatus,
        }, commandTimeout: _commandTimeout);
        return rows > 0;
    }

    public async Task EnableHeartbeatOnEndpoint(string endpointId, bool enable)
    {
        await using var conn = Open();
        await conn.ExecuteAsync(
            $@"MERGE {T("EndpointMetadata")} AS target
               USING (SELECT @EndpointId AS EndpointId) AS source ON target.EndpointId = source.EndpointId
               WHEN MATCHED THEN UPDATE SET IsHeartbeatEnabled = @Enable, UpdatedAtUtc = SYSUTCDATETIME()
               WHEN NOT MATCHED THEN INSERT (EndpointId, IsHeartbeatEnabled) VALUES (@EndpointId, @Enable);",
            new { EndpointId = endpointId, Enable = enable }, commandTimeout: _commandTimeout);
    }

    public async Task<bool> SetHeartbeat(Heartbeat heartbeat, string endpointId)
    {
        var sql = $@"
INSERT INTO {T("Heartbeats")} (EndpointId, MessageId, StartTimeUtc, ReceivedTimeUtc, EndTimeUtc, EndpointHeartbeatStatus)
VALUES (@EndpointId, @MessageId, @StartTime, @ReceivedTime, @EndTime, @Status)";
        await using var conn = Open();
        var rows = await conn.ExecuteAsync(sql, new
        {
            EndpointId = endpointId,
            heartbeat.MessageId,
            StartTime = heartbeat.StartTime,
            ReceivedTime = heartbeat.ReceivedTime,
            EndTime = heartbeat.EndTime,
            Status = heartbeat.EndpointHeartbeatStatus.ToString(),
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
        EndpointHeartbeatStatus = Enum.TryParse((string?)row.EndpointHeartbeatStatus, out HeartbeatStatus hs) ? hs : HeartbeatStatus.Unknown,
        TechnicalContacts = string.IsNullOrEmpty((string?)row.TechnicalContactsJson)
            ? new List<TechnicalContact>()
            : JsonConvert.DeserializeObject<List<TechnicalContact>>((string)row.TechnicalContactsJson) ?? new List<TechnicalContact>(),
        SubscriptionStatus = row.SubscriptionStatus,
    };

    // ───────── Metrics ─────────

    public async Task<EndpointMetricsResult> GetEndpointMetrics(DateTime from)
    {
        var sql = $@"
SELECT EndpointId, EventTypeId, MessageType, EventCount
FROM {T("EndpointEventTypeCounts")}";
        await using var conn = Open();
        var rows = await conn.QueryAsync<(string EndpointId, string EventTypeId, string MessageType, long EventCount)>(sql, commandTimeout: _commandTimeout);
        var result = new EndpointMetricsResult();
        foreach (var r in rows)
        {
            var bucket = r.MessageType switch
            {
                "EventRequest" => result.Published,
                "ResolutionResponse" => result.Handled,
                "ErrorResponse" => result.Failed,
                _ => null,
            };
            bucket?.Add(new EndpointEventTypeCount { EndpointId = r.EndpointId, EventTypeId = r.EventTypeId, Count = (int)r.EventCount });
        }
        return result;
    }

    public Task<EndpointLatencyMetricsResult> GetEndpointLatencyMetrics(DateTime from)
        => Task.FromResult(new EndpointLatencyMetricsResult());

    public async Task<List<FailedMessageInfo>> GetFailedMessageInsights(DateTime from)
    {
        await using var conn = Open();
        var rows = await conn.QueryAsync<FailedMessageInfo>(
            $"SELECT EndpointId, EventTypeId, ErrorText, EnqueuedTimeUtc, EventId FROM {T("FailedMessageInsights")} WHERE EnqueuedTimeUtc >= @From",
            new { From = from }, commandTimeout: _commandTimeout);
        return rows.ToList();
    }

    public Task<TimeSeriesResult> GetTimeSeriesMetrics(DateTime from, int substringLength, string bucketLabel)
        => Task.FromResult(new TimeSeriesResult { BucketSize = bucketLabel });
}
