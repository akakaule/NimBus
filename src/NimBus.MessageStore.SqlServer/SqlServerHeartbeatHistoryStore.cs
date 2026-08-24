using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.SqlServer;

internal sealed class SqlServerHeartbeatHistoryStore : IHeartbeatHistoryStore
{
    private readonly SqlServerStoreContext _context;

    public SqlServerHeartbeatHistoryStore(SqlServerStoreContext context) => _context = context;

    private Task<SqlConnection> OpenAsync() => _context.Open();

    private string T(string table) => _context.Table(table);
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
            commandTimeout: _context.CommandTimeout);
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
        await conn.ExecuteAsync(sql, rows, commandTimeout: _context.CommandTimeout);
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
            commandTimeout: _context.CommandTimeout);
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
        await conn.ExecuteAsync(sql, rows, commandTimeout: _context.CommandTimeout);
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
            commandTimeout: _context.CommandTimeout);
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
            commandTimeout: _context.CommandTimeout);
    }


}
