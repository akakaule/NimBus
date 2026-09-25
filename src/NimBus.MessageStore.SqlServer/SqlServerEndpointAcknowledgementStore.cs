using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.SqlServer;

/// <summary>
/// SQL Server implementation of <see cref="IEndpointAcknowledgementStore"/>: one row per
/// endpoint in <c>EndpointAcknowledgements</c> (migration 0020).
/// </summary>
internal sealed class SqlServerEndpointAcknowledgementStore : IEndpointAcknowledgementStore
{
    private readonly SqlServerStoreContext _context;

    public SqlServerEndpointAcknowledgementStore(SqlServerStoreContext context) => _context = context;

    private string T(string table) => _context.Table(table);

    public async Task<IReadOnlyList<EndpointAcknowledgement>> GetEndpointAcknowledgements()
    {
        await using var conn = await _context.Open();
        var rows = await conn.QueryAsync<AcknowledgementRow>(
            $@"SELECT EndpointId, AcknowledgementId, Reason, AcknowledgedBy, AcknowledgedAtUtc, ExpiresAtUtc, FailedCountAtAcknowledgement
               FROM {T("EndpointAcknowledgements")}",
            commandTimeout: _context.CommandTimeout);

        // datetime2 reads back as DateTimeKind.Unspecified; the columns hold UTC.
        return rows.Select(row => new EndpointAcknowledgement
        {
            EndpointId = row.EndpointId,
            AcknowledgementId = row.AcknowledgementId,
            Reason = row.Reason,
            AcknowledgedBy = row.AcknowledgedBy,
            AcknowledgedAtUtc = DateTime.SpecifyKind(row.AcknowledgedAtUtc, DateTimeKind.Utc),
            ExpiresAtUtc = DateTime.SpecifyKind(row.ExpiresAtUtc, DateTimeKind.Utc),
            FailedCountAtAcknowledgement = row.FailedCountAtAcknowledgement,
        }).ToList();
    }

    public async Task SetEndpointAcknowledgement(EndpointAcknowledgement acknowledgement)
    {
        var parameters = new DynamicParameters();
        parameters.Add("EndpointId", acknowledgement.EndpointId);
        parameters.Add("AcknowledgementId", acknowledgement.AcknowledgementId);
        parameters.Add("Reason", acknowledgement.Reason ?? string.Empty);
        parameters.Add("AcknowledgedBy", acknowledgement.AcknowledgedBy);
        // Explicit DateTime2: SqlClient otherwise binds DateTime as legacy DATETIME (~3.33 ms rounding).
        parameters.Add("AcknowledgedAtUtc", acknowledgement.AcknowledgedAtUtc, DbType.DateTime2);
        parameters.Add("ExpiresAtUtc", acknowledgement.ExpiresAtUtc, DbType.DateTime2);
        parameters.Add("FailedCountAtAcknowledgement", acknowledgement.FailedCountAtAcknowledgement);

        await using var conn = await _context.Open();
        await conn.ExecuteAsync(
            $@"MERGE {T("EndpointAcknowledgements")} WITH (HOLDLOCK) AS target
               USING (SELECT @EndpointId AS EndpointId) AS source
               ON target.EndpointId = source.EndpointId
               WHEN MATCHED THEN
                   UPDATE SET AcknowledgementId = @AcknowledgementId, Reason = @Reason, AcknowledgedBy = @AcknowledgedBy,
                              AcknowledgedAtUtc = @AcknowledgedAtUtc, ExpiresAtUtc = @ExpiresAtUtc,
                              FailedCountAtAcknowledgement = @FailedCountAtAcknowledgement
               WHEN NOT MATCHED THEN
                   INSERT (EndpointId, AcknowledgementId, Reason, AcknowledgedBy, AcknowledgedAtUtc, ExpiresAtUtc, FailedCountAtAcknowledgement)
                   VALUES (@EndpointId, @AcknowledgementId, @Reason, @AcknowledgedBy, @AcknowledgedAtUtc, @ExpiresAtUtc, @FailedCountAtAcknowledgement);",
            parameters,
            commandTimeout: _context.CommandTimeout);
    }

    public async Task<bool> RemoveEndpointAcknowledgement(string endpointId, string? expectedAcknowledgementId = null)
    {
        await using var conn = await _context.Open();
        var rows = await conn.ExecuteAsync(
            $@"DELETE FROM {T("EndpointAcknowledgements")}
               WHERE EndpointId = @EndpointId
                 AND (@ExpectedAcknowledgementId IS NULL OR AcknowledgementId = @ExpectedAcknowledgementId)",
            new { EndpointId = endpointId, ExpectedAcknowledgementId = expectedAcknowledgementId },
            commandTimeout: _context.CommandTimeout);
        return rows > 0;
    }

    private sealed class AcknowledgementRow
    {
        public string EndpointId { get; set; } = string.Empty;
        public string AcknowledgementId { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string? AcknowledgedBy { get; set; }
        public DateTime AcknowledgedAtUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public int FailedCountAtAcknowledgement { get; set; }
    }
}
