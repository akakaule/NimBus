using Dapper;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.SqlServer;

internal sealed partial class SqlServerMessageTrackingStore
{
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
        var rows = await conn.QueryAsync(sql, p, commandTimeout: _context.CommandTimeout);
        var events = rows.Select(MapUnresolvedEventRow).ToList();

        return new SearchResponse
        {
            Events = events,
            ContinuationToken = events.Count == pageSize ? EncodeOffset(offset + pageSize) : null!,
        };
    }

    public async Task<IEnumerable<UnresolvedEvent>> GetPendingEventsOnSession(string endpointId)
    {
        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(
            $"SELECT * FROM {T("UnresolvedEvents")} WHERE EndpointId = @E AND Status = 'Pending' AND Deleted = 0",
            new { E = endpointId }, commandTimeout: _context.CommandTimeout);
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
            commandTimeout: _context.CommandTimeout);

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
            commandTimeout: _context.CommandTimeout);

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
    PendingSubStatus, HandoffReason, ExternalJobId, ExpectedBy,
    DeadLetterReason, DeadLetterErrorDescription,
    JSON_MODIFY(MessageContentJson, '$.EventContent.EventJson', NULL) AS MessageContentJson
FROM {T("Messages")}
WHERE {string.Join(" AND ", where)}
ORDER BY EnqueuedTimeUtc DESC, Id DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(sql, p, commandTimeout: _context.CommandTimeout);
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
            commandTimeout: _context.CommandTimeout);

        return EndpointErrorListFormat.Format(ids.ToList());
    }
}
