#pragma warning disable CA1707, CA2007
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.SqlServer;
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
    private static string? _connectionString;
    private static readonly string TestSchema = $"nimbus_test_{Guid.NewGuid():N}".Substring(0, 16);

    [ClassInitialize]
    public static async Task ClassInit(TestContext context)
    {
        _connectionString = Environment.GetEnvironmentVariable("NIMBUS_SQL_TEST_CONNECTION");
        if (string.IsNullOrEmpty(_connectionString))
        {
            Assert.Inconclusive("NIMBUS_SQL_TEST_CONNECTION not set; skipping SQL Server conformance suite.");
            return;
        }

        var options = Options.Create(new SqlServerMessageStoreOptions
        {
            ConnectionString = _connectionString,
            Schema = TestSchema,
            ProvisioningMode = SchemaProvisioningMode.AutoApply,
        });
        var initializer = new SqlServerSchemaInitializer(options, NullLogger<SqlServerSchemaInitializer>.Instance);
        await initializer.StartAsync(CancellationToken.None);
    }

    protected override IMessageTrackingStore CreateStore()
    {
        if (string.IsNullOrEmpty(_connectionString))
            Assert.Inconclusive("NIMBUS_SQL_TEST_CONNECTION not set");
        return new SqlServerMessageStore(Options.Create(new SqlServerMessageStoreOptions
        {
            ConnectionString = _connectionString!,
            Schema = TestSchema,
            ProvisioningMode = SchemaProvisioningMode.VerifyOnly,
        }));
    }
}
