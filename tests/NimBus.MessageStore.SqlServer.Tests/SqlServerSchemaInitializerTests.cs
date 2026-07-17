#pragma warning disable CA1707, CA2007
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.SqlServer;

namespace NimBus.MessageStore.SqlServer.Tests;

[TestClass]
public sealed class SqlServerSchemaInitializerTests
{
    [TestMethod]
    public async Task VerifyOnly_on_empty_database_fails_fast_with_missing_artifacts()
    {
        var schema = NewSchemaName();
        var initializer = CreateInitializer(schema, SchemaProvisioningMode.VerifyOnly);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => initializer.StartAsync(CancellationToken.None));

        StringAssert.Contains(ex.Message, "Missing artifacts");
        StringAssert.Contains(ex.Message, $"schema '{schema}'");
        StringAssert.Contains(ex.Message, $"[{schema}].[DbUpJournal]");
        StringAssert.Contains(ex.Message, $"[{schema}].[Messages]");
        StringAssert.Contains(ex.Message, $"[{schema}].[UnresolvedEvents]");
    }

    [TestMethod]
    public async Task AutoApply_can_run_twice_on_same_connection_as_no_op()
    {
        var schema = NewSchemaName();
        var first = CreateInitializer(schema, SchemaProvisioningMode.AutoApply);
        var second = CreateInitializer(schema, SchemaProvisioningMode.AutoApply);
        var verify = CreateInitializer(schema, SchemaProvisioningMode.VerifyOnly);

        await first.StartAsync(CancellationToken.None);
        await second.StartAsync(CancellationToken.None);
        await verify.StartAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task AutoApply_creates_date_leading_search_sort_indexes()
    {
        var schema = NewSchemaName();
        var initializer = CreateInitializer(schema, SchemaProvisioningMode.AutoApply);

        await initializer.StartAsync(CancellationToken.None);

        Assert.IsTrue(
            await IndexExists(schema, "Messages", "IX_Messages_EnqueuedTimeUtc"),
            "IX_Messages_EnqueuedTimeUtc should exist after provisioning.");
        Assert.IsTrue(
            await IndexExists(schema, "UnresolvedEvents", "IX_UnresolvedEvents_UpdatedAtUtc"),
            "IX_UnresolvedEvents_UpdatedAtUtc should exist after provisioning.");
        Assert.IsTrue(
            await IndexExists(schema, "MessageAudits", "IX_MessageAudits_CreatedAtUtc"),
            "IX_MessageAudits_CreatedAtUtc should exist after provisioning.");
    }

    [TestMethod]
    public async Task AutoApply_adds_nullable_CloudEvent_columns_to_tracking_and_audit_tables()
    {
        var schema = NewSchemaName();
        var initializer = CreateInitializer(schema, SchemaProvisioningMode.AutoApply);

        await initializer.StartAsync(CancellationToken.None);

        foreach (var table in new[] { "Messages", "UnresolvedEvents", "MessageAudits" })
        {
            foreach (var column in new[] { "CloudEventId", "CloudEventSource", "CloudEventType", "CloudEventSubject" })
            {
                Assert.IsTrue(await NullableColumnExists(schema, table, column),
                    $"[{schema}].[{table}].[{column}] should exist and remain nullable for legacy native-message rows.");
            }
        }
    }

    private static async Task<bool> IndexExists(string schema, string table, string indexName)
    {
        await using var conn = new SqlConnection(SqlServerStoreTestHarness.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT COUNT(1)
FROM sys.indexes i
INNER JOIN sys.tables t ON i.object_id = t.object_id
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
WHERE s.name = @Schema AND t.name = @Table AND i.name = @Index";
        cmd.Parameters.AddWithValue("@Schema", schema);
        cmd.Parameters.AddWithValue("@Table", table);
        cmd.Parameters.AddWithValue("@Index", indexName);
        var result = await cmd.ExecuteScalarAsync();
        return result is int count && count > 0;
    }

    private static async Task<bool> NullableColumnExists(string schema, string table, string column)
    {
        await using var conn = new SqlConnection(SqlServerStoreTestHarness.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT COUNT(1)
FROM sys.columns c
INNER JOIN sys.tables t ON c.object_id = t.object_id
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
WHERE s.name = @Schema AND t.name = @Table AND c.name = @Column AND c.is_nullable = 1";
        cmd.Parameters.AddWithValue("@Schema", schema);
        cmd.Parameters.AddWithValue("@Table", table);
        cmd.Parameters.AddWithValue("@Column", column);
        var result = await cmd.ExecuteScalarAsync();
        return result is int count && count > 0;
    }

    private static SqlServerSchemaInitializer CreateInitializer(string schema, SchemaProvisioningMode mode)
        => new(
            Options.Create(new SqlServerMessageStoreOptions
            {
                ConnectionString = SqlServerStoreTestHarness.GetConnectionString(),
                Schema = schema,
                ProvisioningMode = mode,
            }),
            NullLogger<SqlServerSchemaInitializer>.Instance);

    private static string NewSchemaName()
        => $"nimbus_test_{Guid.NewGuid():N}"[..24];
}
