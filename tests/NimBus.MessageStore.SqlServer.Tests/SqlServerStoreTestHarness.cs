#pragma warning disable CA2007
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.SqlServer;

namespace NimBus.MessageStore.SqlServer.Tests;

internal static class SqlServerStoreTestHarness
{
    private static readonly ConcurrentDictionary<string, string> Schemas = new();

    public static async Task InitializeAsync(Type testType)
    {
        var connectionString = GetConnectionString();
        var initializer = new SqlServerSchemaInitializer(
            Options.Create(new SqlServerMessageStoreOptions
            {
                ConnectionString = connectionString,
                Schema = GetSchema(testType),
                ProvisioningMode = SchemaProvisioningMode.AutoApply,
            }),
            NullLogger<SqlServerSchemaInitializer>.Instance);

        await initializer.StartAsync(CancellationToken.None);
    }

    public static async Task ResetAsync(Type testType)
    {
        var connectionString = GetConnectionString();
        var schema = GetSchema(testType);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        var sql = $@"
            TRUNCATE TABLE [{schema}].[Messages];
            TRUNCATE TABLE [{schema}].[UnresolvedEvents];
            TRUNCATE TABLE [{schema}].[MessageAudits];
            TRUNCATE TABLE [{schema}].[EndpointSubscriptions];
            TRUNCATE TABLE [{schema}].[EndpointMetadata];
            TRUNCATE TABLE [{schema}].[Heartbeats];
            TRUNCATE TABLE [{schema}].[BlockedMessages];
            TRUNCATE TABLE [{schema}].[InvalidMessages];";
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public static INimBusMessageStore CreateStore(Type testType)
        => new SqlServerMessageStore(Options.Create(new SqlServerMessageStoreOptions
        {
            ConnectionString = GetConnectionString(),
            Schema = GetSchema(testType),
            ProvisioningMode = SchemaProvisioningMode.VerifyOnly,
        }));

    public static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("NIMBUS_SQL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("NIMBUS_SQL_TEST_CONNECTION not set; skipping SQL Server conformance suite.");
        }

        return connectionString!;
    }

    public static string GetSchema(Type testType)
        => Schemas.GetOrAdd(testType.FullName ?? testType.Name, _ => $"nimbus_test_{Guid.NewGuid():N}"[..24]);
}
