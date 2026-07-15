#pragma warning disable CA1707, CA2007
using System;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Outbox;
using NimBus.Outbox.SqlServer;
using NimBus.Testing.Conformance;

namespace NimBus.Outbox.SqlServer.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SqlServerOutboxConformanceTests : OutboxConformanceTests
{
    private static readonly string Schema = $"nimbus_obx_ct_{Guid.NewGuid():N}"[..24];
    private SqlServerOutboxOptions _options = null!;

    protected override async Task<IOutbox> CreateOutboxAsync()
    {
        _options = new SqlServerOutboxOptions
        {
            ConnectionString = GetConnectionString(),
            Schema = Schema,
        };

        var outbox = new SqlServerOutbox(_options);
        await outbox.EnsureTableExistsAsync();

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"DELETE FROM {_options.FullTableName}", connection);
        await command.ExecuteNonQueryAsync();
        return outbox;
    }

    protected override async Task<DateTime?> GetDispatchedAtUtcAsync(string id)
    {
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            $"SELECT [DispatchedAtUtc] FROM {_options.FullTableName} WHERE [Id] = @Id",
            connection);
        command.Parameters.AddWithValue("@Id", id);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : (DateTime)result;
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("NIMBUS_SQL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("NIMBUS_SQL_TEST_CONNECTION not set; skipping SQL Server outbox conformance tests.");
        }

        return connectionString;
    }
}
