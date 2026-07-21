#pragma warning disable CA1707, CA2007
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Inbox;
using NimBus.Testing.Conformance;

namespace NimBus.Inbox.SqlServer.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SqlServerInboxConformanceTests : InboxStoreConformanceTests
{
    private static readonly string Schema = $"nimbus_inbox_ct_{Guid.NewGuid():N}"[..24];
    private string _connectionString = null!;

    protected override async Task<IInboxStore> CreateStoreAsync()
    {
        _connectionString = GetConnectionString();
        var options = new SqlServerInboxOptions
        {
            ConnectionString = _connectionString,
            Schema = Schema,
        };
        var store = new SqlServerInbox(options);
        await store.EnsureTableExistsAsync();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"DELETE FROM {options.FullTableName}", connection);
        await command.ExecuteNonQueryAsync();
        return store;
    }

    protected override async Task<DateTimeOffset> AdvancePastFirstRecordAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT SYSUTCDATETIME()", connection);
        var value = (DateTime)(await command.ExecuteScalarAsync())!;
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("NIMBUS_SQL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("NIMBUS_SQL_TEST_CONNECTION not set; skipping SQL Server inbox conformance tests.");
        }

        return connectionString;
    }
}

[TestClass]
[DoNotParallelize]
public sealed class SqlServerInboxSchemaTests
{
    [TestMethod]
    public async Task First_data_operation_lazily_creates_the_table()
    {
        var options = new SqlServerInboxOptions
        {
            ConnectionString = GetConnectionString(),
            Schema = $"nimbus_inbox_{Guid.NewGuid():N}"[..24],
        };
        var store = new SqlServerInbox(options);

        Assert.IsFalse(await store.HasProcessedAsync("billing", "not-recorded"));

        Assert.IsTrue(await TableExistsAsync(options));
    }

    [TestMethod]
    public async Task PurgeExpiredAsync_deletes_at_most_one_thousand_rows_per_call()
    {
        var options = new SqlServerInboxOptions
        {
            ConnectionString = GetConnectionString(),
            Schema = $"nimbus_inbox_{Guid.NewGuid():N}"[..24],
        };
        var store = new SqlServerInbox(options);
        await store.EnsureTableExistsAsync();

        await using (var connection = new SqlConnection(options.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                WITH [Numbers] AS
                (
                    SELECT 0 AS [Value]
                    UNION ALL
                    SELECT [Value] + 1 FROM [Numbers] WHERE [Value] < 1000
                )
                INSERT INTO {options.FullTableName} ([EndpointId], [MessageId], [CreatedAtUtc])
                SELECT N'billing', N'purge-' + CONVERT(NVARCHAR(10), [Value]), @CreatedAtUtc
                FROM [Numbers]
                OPTION (MAXRECURSION 1001);
                """;
            command.Parameters.Add("@CreatedAtUtc", SqlDbType.DateTime2).Value = new DateTime(2000, 1, 1);
            Assert.AreEqual(1_001, await command.ExecuteNonQueryAsync());
        }

        Assert.AreEqual(1_000, await store.PurgeExpiredAsync(DateTimeOffset.UtcNow));
        Assert.AreEqual(1, await store.PurgeExpiredAsync(DateTimeOffset.UtcNow));
        Assert.AreEqual(0, await store.PurgeExpiredAsync(DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public async Task EnsureTableExistsAsync_is_idempotent_and_creates_primary_key_and_purge_index()
    {
        var options = new SqlServerInboxOptions
        {
            ConnectionString = GetConnectionString(),
            Schema = $"nimbus_inbox_{Guid.NewGuid():N}"[..24],
        };
        var store = new SqlServerInbox(options);

        await store.EnsureTableExistsAsync();
        await store.EnsureTableExistsAsync();

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // The primary key must be NONCLUSTERED: the composite (EndpointId, MessageId) key can
        // reach 1,544 bytes, past the 900-byte clustered-key limit but within the 1,700-byte
        // nonclustered limit. CreatedAtUtc carries the clustered index for bounded purges.
        command.CommandText = """
            SELECT
                SUM(CASE WHEN i.is_primary_key = 1 AND i.type_desc = N'NONCLUSTERED' AND c.name = N'EndpointId' AND ty.name = N'nvarchar' AND c.max_length = 520 AND c.collation_name = N'Latin1_General_100_BIN2' THEN 1 ELSE 0 END),
                SUM(CASE WHEN i.is_primary_key = 1 AND i.type_desc = N'NONCLUSTERED' AND c.name = N'MessageId' AND ty.name = N'nvarchar' AND c.max_length = 1024 AND c.collation_name = N'Latin1_General_100_BIN2' THEN 1 ELSE 0 END),
                SUM(CASE WHEN i.is_primary_key = 0 AND i.type_desc = N'CLUSTERED' AND c.name = N'CreatedAtUtc' AND ty.name = N'datetime2' AND c.is_nullable = 0 THEN 1 ELSE 0 END),
                SUM(CASE WHEN c.name = N'CreatedAtUtc' AND dc.definition LIKE N'%sysutcdatetime%' THEN 1 ELSE 0 END)
            FROM sys.tables t
            INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
            INNER JOIN sys.indexes i ON i.object_id = t.object_id
            INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            INNER JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
            WHERE s.name = @Schema AND t.name = @TableName;
            """;
        command.Parameters.Add("@Schema", SqlDbType.NVarChar, 128).Value = options.Schema;
        command.Parameters.Add("@TableName", SqlDbType.NVarChar, 128).Value = options.TableName;

        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(1, reader.GetInt32(0));
        Assert.AreEqual(1, reader.GetInt32(1));
        Assert.AreEqual(1, reader.GetInt32(2));
        Assert.AreEqual(1, reader.GetInt32(3));
    }

    [TestMethod]
    public async Task PurgeExpiredAsync_succeeds_on_a_read_committed_snapshot_database()
    {
        // READPAST alone is rejected under READ_COMMITTED_SNAPSHOT (Azure SQL's default);
        // the purge must pair it with READCOMMITTEDLOCK. Exercised against a dedicated
        // database with RCSI enabled.
        var serverConnectionString = GetConnectionString();
        var databaseName = $"nimbus_inbox_rcsi_{Guid.NewGuid():N}"[..30];
        await using (var connection = new SqlConnection(serverConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new SqlCommand(
                $"CREATE DATABASE [{databaseName}]; ALTER DATABASE [{databaseName}] SET READ_COMMITTED_SNAPSHOT ON;",
                connection);
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var builder = new SqlConnectionStringBuilder(serverConnectionString)
            {
                InitialCatalog = databaseName,
            };
            var options = new SqlServerInboxOptions { ConnectionString = builder.ConnectionString };
            using var store = new SqlServerInbox(options);

            Assert.IsTrue(await IsReadCommittedSnapshotOnAsync(builder.ConnectionString, databaseName));

            await store.RecordProcessedAsync("billing", "rcsi-message");
            Assert.AreEqual(1, await store.PurgeExpiredAsync(DateTimeOffset.UtcNow.AddMinutes(1)));
            Assert.IsFalse(await store.HasProcessedAsync("billing", "rcsi-message"));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await using var connection = new SqlConnection(serverConnectionString);
            await connection.OpenAsync();
            await using var drop = new SqlCommand(
                $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];",
                connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<bool> IsReadCommittedSnapshotOnAsync(string connectionString, string databaseName)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = @Name;",
            connection);
        command.Parameters.Add("@Name", SqlDbType.NVarChar, 128).Value = databaseName;
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("NIMBUS_SQL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("NIMBUS_SQL_TEST_CONNECTION not set; skipping SQL Server inbox schema tests.");
        }

        return connectionString;
    }

    private static async Task<bool> TableExistsAsync(SqlServerInboxOptions options)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(1)
            FROM sys.tables t
            INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = @Schema AND t.name = @TableName;
            """;
        command.Parameters.Add("@Schema", SqlDbType.NVarChar, 128).Value = options.Schema;
        command.Parameters.Add("@TableName", SqlDbType.NVarChar, 128).Value = options.TableName;
        return (int)(await command.ExecuteScalarAsync())! == 1;
    }
}
