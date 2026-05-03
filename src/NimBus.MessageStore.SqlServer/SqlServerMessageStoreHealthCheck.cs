using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace NimBus.MessageStore.SqlServer;

/// <summary>
/// Health check that verifies SQL Server connectivity and the presence of the
/// schema's journal table (which proves DbUp ran). Mirrors the Cosmos provider's
/// CosmosDbHealthCheck contract so the WebApp's /ready endpoint behaves identically
/// regardless of provider.
/// </summary>
public sealed class SqlServerMessageStoreHealthCheck : IHealthCheck
{
    private readonly SqlServerMessageStoreOptions _options;

    public SqlServerMessageStoreHealthCheck(IOptions<SqlServerMessageStoreOptions> options)
    {
        _options = options.Value;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = new SqlConnection(_options.ConnectionString);
            await conn.OpenAsync(cancellationToken);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"SELECT CASE WHEN OBJECT_ID('[{_options.Schema}].[DbUpJournal]', 'U') IS NOT NULL THEN 1 ELSE 0 END";
            var present = (int?)await cmd.ExecuteScalarAsync(cancellationToken);
            return present == 1
                ? HealthCheckResult.Healthy("SQL Server message store is reachable and schema is present.")
                : HealthCheckResult.Degraded($"Connected but schema '{_options.Schema}' is not provisioned. Run DbUp.");
        }
        catch (System.Exception ex)
        {
            return HealthCheckResult.Unhealthy("SQL Server message store unreachable.", ex);
        }
    }
}
