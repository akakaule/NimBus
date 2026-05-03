using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DbUp;
using DbUp.Engine;
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

        // DbUp's journal table lives in the configured schema, and EnsureTableExistsAndIsLatestVersion
        // runs before any DbUp script — including the one that creates the schema. Bootstrap the
        // schema directly so DbUp's journal creation has somewhere to land.
        await using (var conn = new SqlConnection(_options.ConnectionString))
        {
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = @schema) EXEC('CREATE SCHEMA [{_options.Schema.Replace("]", "]]", StringComparison.Ordinal)}]')";
            cmd.Parameters.Add(new SqlParameter("@schema", _options.Schema));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
}
