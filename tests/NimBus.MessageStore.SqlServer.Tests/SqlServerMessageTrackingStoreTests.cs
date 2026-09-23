#pragma warning disable CA1707, CA2007
using System.Threading.Tasks;
using System;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;

namespace NimBus.MessageStore.SqlServer.Tests;

/// <summary>
/// SQL Server conformance run. Skipped automatically when no
/// <c>NIMBUS_SQL_TEST_CONNECTION</c> env var is set, so contributors without a
/// running SQL Server can still run the rest of the suite. CI sets the env var
/// to point at the Linux SQL Server service container.
/// </summary>
[TestClass]
public sealed class SqlServerMessageTrackingStoreTests : MessageTrackingStoreConformanceTests
{
    [ClassInitialize]
    public static Task ClassInit(TestContext context)
        => SqlServerStoreTestHarness.InitializeAsync(typeof(SqlServerMessageTrackingStoreTests));

    [TestInitialize]
    public Task ResetSchema()
        => SqlServerStoreTestHarness.ResetAsync(typeof(SqlServerMessageTrackingStoreTests));

    protected override IMessageTrackingStore CreateStore()
        => SqlServerStoreTestHarness.CreateStore(typeof(SqlServerMessageTrackingStoreTests));

    [TestMethod]
    public async Task TrySkipDeferredMessage_preserves_datetime2_version_precision()
    {
        var store = CreateStore();
        await store.UploadDeferredMessage("precision-event", "session", "endpoint", new UnresolvedEvent
        {
            EventId = "precision-event", SessionId = "session", EndpointId = "endpoint",
            LastMessageId = "deferral", EnqueuedTimeUtc = DateTime.UtcNow,
        });
        await using var connection = new SqlConnection(SqlServerStoreTestHarness.GetConnectionString());
        await connection.OpenAsync();
        var schema = SqlServerStoreTestHarness.GetSchema(typeof(SqlServerMessageTrackingStoreTests));
        await using var command = new SqlCommand($"UPDATE [{schema}].[UnresolvedEvents] SET UpdatedAtUtc = CAST('2026-09-23T00:00:00.1234567' AS datetime2) WHERE EventId = @EventId", connection);
        command.Parameters.AddWithValue("@EventId", "precision-event");
        await command.ExecuteNonQueryAsync();
        var inspected = await store.GetEvent("endpoint", "precision-event");
        Assert.IsTrue(await store.TrySkipDeferredMessage("precision-event", "session", "endpoint", "deferral", inspected.UpdatedAt));
    }
}
