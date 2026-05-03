#pragma warning disable CA1707, CA2007
using System.Threading.Tasks;
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
}
