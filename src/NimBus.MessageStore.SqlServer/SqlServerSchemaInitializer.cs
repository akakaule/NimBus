using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DbUp;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NimBus.MessageStore.SqlServer;

/// <summary>
/// Hosted service that runs DbUp on startup. Behavior is governed by
/// <see cref="SqlServerMessageStoreOptions.ProvisioningMode"/>:
/// <c>AutoApply</c> upgrades the schema; <c>VerifyOnly</c> fails fast if any
/// embedded script is unapplied.
/// </summary>
internal sealed class SqlServerSchemaInitializer : IHostedService
{
    private static readonly string[] RequiredTables =
    {
        "DbUpJournal",
        "Messages",
        "UnresolvedEvents",
        "MessageAudits",
        "EndpointSubscriptions",
        "EndpointMetadata",
        "Heartbeats",
        "BlockedMessages",
        "InvalidMessages",
    };

    private static readonly string[] RequiredViews =
    {
        "EndpointEventTypeCounts",
        "FailedMessageInsights",
    };

    private readonly SqlServerMessageStoreOptions _options;
    private readonly ILogger<SqlServerSchemaInitializer> _logger;

    public SqlServerSchemaInitializer(IOptions<SqlServerMessageStoreOptions> options, ILogger<SqlServerSchemaInitializer> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
            throw new InvalidOperationException("SqlServerMessageStoreOptions.ConnectionString is required.");

        if (string.IsNullOrWhiteSpace(_options.Schema))
            throw new InvalidOperationException("SqlServerMessageStoreOptions.Schema is required.");

        if (_options.ProvisioningMode == SchemaProvisioningMode.VerifyOnly)
        {
            await VerifyRequiredArtifacts(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // DbUp's journal table lives in the configured schema, and journal setup
            // runs before any DbUp script. Bootstrap the schema directly so journal
            // creation has somewhere to land; 0001_Schema.sql remains idempotent.
            await EnsureSchemaExists(cancellationToken).ConfigureAwait(false);
        }

        var assembly = typeof(SqlServerSchemaInitializer).Assembly;

        var upgrader = DeployChanges.To
            .SqlDatabase(_options.ConnectionString)
            .WithScriptsEmbeddedInAssembly(
                assembly,
                name => name.Contains(".Schema.", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .WithVariable("schema", _options.Schema)
            .JournalToSqlTable(_options.Schema, "DbUpJournal")
            .LogToConsole()
            .Build();

        if (_options.ProvisioningMode == SchemaProvisioningMode.VerifyOnly)
        {
            var pending = upgrader.GetScriptsToExecute().Select(s => s.Name).ToList();
            if (pending.Count > 0)
            {
                throw new InvalidOperationException(
                    "SQL Server schema is out of date. Run DbUp scripts via the deployment pipeline, " +
                    "or switch to ProvisioningMode.AutoApply for development. Pending scripts: " +
                    string.Join(", ", pending));
            }
            _logger.LogInformation("SQL Server message-store schema verified ({Count} scripts already applied).",
                upgrader.GetExecutedScripts().Count);
            return;
        }

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
        {
            throw new InvalidOperationException(
                $"DbUp failed to apply NimBus message-store schema: {result.Error?.Message}", result.Error);
        }

        _logger.LogInformation("SQL Server message-store schema upgrade applied {Count} script(s).",
            result.Scripts.Count());
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task EnsureSchemaExists(CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = @Schema)
BEGIN
    DECLARE @sql nvarchar(max) = N'CREATE SCHEMA ' + QUOTENAME(@Schema);
    EXEC sp_executesql @sql;
END";
        cmd.Parameters.AddWithValue("@Schema", _options.Schema);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyRequiredArtifacts(CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var missing = new List<string>();
        if (!await SchemaExists(conn, cancellationToken).ConfigureAwait(false))
        {
            missing.Add($"schema '{_options.Schema}'");
            missing.AddRange(RequiredTables.Select(Qualified));
            missing.AddRange(RequiredViews.Select(Qualified));
        }
        else
        {
            foreach (var table in RequiredTables)
            {
                if (!await ObjectExists(conn, "U", table, cancellationToken).ConfigureAwait(false))
                    missing.Add(Qualified(table));
            }

            foreach (var view in RequiredViews)
            {
                if (!await ObjectExists(conn, "V", view, cancellationToken).ConfigureAwait(false))
                    missing.Add(Qualified(view));
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "SQL Server message-store schema is missing or out of date. Missing artifacts: " +
                string.Join(", ", missing));
        }
    }

    private async Task<bool> SchemaExists(SqlConnection conn, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM sys.schemas WHERE name = @Schema";
        cmd.Parameters.AddWithValue("@Schema", _options.Schema);
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result) > 0;
    }

    private async Task<bool> ObjectExists(SqlConnection conn, string type, string name, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT COUNT(1)
FROM sys.objects o
INNER JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE s.name = @Schema AND o.name = @Name AND o.type = @Type";
        cmd.Parameters.AddWithValue("@Schema", _options.Schema);
        cmd.Parameters.AddWithValue("@Name", name);
        cmd.Parameters.AddWithValue("@Type", type);
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result) > 0;
    }

    private string Qualified(string objectName) => $"[{_options.Schema}].[{objectName}]";
}
