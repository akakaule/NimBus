using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.SqlServer;

internal sealed class SqlServerServiceHealthStore : IServiceHealthStore
{
    private readonly SqlServerStoreContext _context;

    public SqlServerServiceHealthStore(SqlServerStoreContext context) => _context = context;

    private Task<SqlConnection> OpenAsync() => _context.Open();

    private string T(string table) => _context.Table(table);
    // ───────── Service health (platform services, not endpoints) ─────────

    public async Task<List<ServiceHealth>> GetServiceHealth()
    {
        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(
            $@"SELECT ServiceId, Status, Version, LastProbeMessageId, LastProbeSentUtc, LastSeenUtc, RoundTripMs
               FROM {T("ServiceHealth")}
               ORDER BY ServiceId",
            commandTimeout: _context.CommandTimeout);

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
        }, commandTimeout: _context.CommandTimeout);
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
        }, commandTimeout: _context.CommandTimeout);
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
        }, commandTimeout: _context.CommandTimeout);
        return swept.ToList();
    }


}
