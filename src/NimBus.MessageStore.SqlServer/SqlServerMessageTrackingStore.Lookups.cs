using Dapper;

namespace NimBus.MessageStore.SqlServer;

internal sealed partial class SqlServerMessageTrackingStore
{
    // ───────── Single-event lookups ─────────

    public Task<UnresolvedEvent?> GetPendingEvent(string endpointId, string eventId, string sessionId)
        => GetEventByStatus(endpointId, eventId, sessionId, "Pending");

    public Task<UnresolvedEvent?> GetFailedEvent(string endpointId, string eventId, string sessionId)
        => GetEventByStatus(endpointId, eventId, sessionId, "Failed");

    public Task<UnresolvedEvent?> GetDeferredEvent(string endpointId, string eventId, string sessionId)
        => GetEventByStatus(endpointId, eventId, sessionId, "Deferred");

    public Task<UnresolvedEvent?> GetDeadletteredEvent(string endpointId, string eventId, string sessionId)
        => GetEventByStatus(endpointId, eventId, sessionId, "DeadLettered");

    public Task<UnresolvedEvent?> GetUnsupportedEvent(string endpointId, string eventId, string sessionId)
        => GetEventByStatus(endpointId, eventId, sessionId, "Unsupported");

    private async Task<UnresolvedEvent?> GetEventByStatus(string endpointId, string eventId, string sessionId, string status)
    {
        await using var conn = await OpenAsync();
        var row = await conn.QueryFirstOrDefaultAsync(
            $"SELECT * FROM {T("UnresolvedEvents")} WHERE EndpointId = @E AND EventId = @V AND SessionId = @S AND Status = @St AND Deleted = 0",
            new { E = endpointId, V = eventId, S = sessionId, St = status }, commandTimeout: _context.CommandTimeout);
        return row == null ? null : MapUnresolvedEventRow(row);
    }

    public async Task<UnresolvedEvent?> GetEvent(string endpointId, string eventId)
    {
        await using var conn = await OpenAsync();
        var row = await conn.QueryFirstOrDefaultAsync(
            $"SELECT TOP 1 * FROM {T("UnresolvedEvents")} WHERE EndpointId = @E AND EventId = @V AND Deleted = 0 ORDER BY UpdatedAtUtc DESC",
            new { E = endpointId, V = eventId }, commandTimeout: _context.CommandTimeout);
        return row == null ? null : MapUnresolvedEventRow(row);
    }

    public async Task<UnresolvedEvent?> GetPendingHandoffByExternalJobId(string endpointId, string externalJobId, CancellationToken cancellationToken = default)
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
            new { E = endpointId, X = externalJobId }, commandTimeout: _context.CommandTimeout);
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

        var row = await conn.QueryFirstOrDefaultAsync(sql, p, commandTimeout: _context.CommandTimeout);
        return row == null ? null : MapUnresolvedEventRow(row);
    }

    public async Task<UnresolvedEvent?> GetEventById(string endpointId, string id)
    {
        // The stored id is "{EventId}_{SessionId}" (Cosmos document id); match it the same
        // way GetEventsByIds does.
        await using var conn = await OpenAsync();
        var row = await conn.QueryFirstOrDefaultAsync(
            $@"SELECT TOP 1 * FROM {T("UnresolvedEvents")}
               WHERE EndpointId = @E
                 AND CONCAT(EventId, '_', ISNULL(SessionId, '')) = @Id
                 AND Deleted = 0",
            new { E = endpointId, Id = id }, commandTimeout: _context.CommandTimeout);
        return row == null ? null : MapUnresolvedEventRow(row);
    }

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
            new { E = endpointId, Ids = ids }, commandTimeout: _context.CommandTimeout);
        return rows.Select(MapUnresolvedEventRow).ToList();
    }

    public async Task<IEnumerable<UnresolvedEvent>> GetCompletedEventsOnEndpoint(string endpointId)
    {
        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(
            $"SELECT * FROM {T("UnresolvedEvents")} WHERE EndpointId = @E AND Status = 'Completed' AND Deleted = 0",
            new { E = endpointId }, commandTimeout: _context.CommandTimeout);
        return rows.Select(MapUnresolvedEventRow).ToList();
    }
}
