#pragma warning disable CA1707, CA2007
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;

namespace NimBus.MessageStore.SqlServer.Tests;

[TestClass]
public sealed class SqlServerSubscriptionStoreTests : SubscriptionStoreConformanceTests
{
    [ClassInitialize]
    public static Task ClassInit(TestContext context)
        => SqlServerStoreTestHarness.InitializeAsync(typeof(SqlServerSubscriptionStoreTests));

    [TestInitialize]
    public Task ResetSchema()
        => SqlServerStoreTestHarness.ResetAsync(typeof(SqlServerSubscriptionStoreTests));

    protected override ISubscriptionStore CreateStore()
        => SqlServerStoreTestHarness.CreateStore(typeof(SqlServerSubscriptionStoreTests));
}

[TestClass]
public sealed class SqlServerEndpointMetadataStoreTests : EndpointMetadataStoreConformanceTests
{
    [ClassInitialize]
    public static Task ClassInit(TestContext context)
        => SqlServerStoreTestHarness.InitializeAsync(typeof(SqlServerEndpointMetadataStoreTests));

    [TestInitialize]
    public Task ResetSchema()
        => SqlServerStoreTestHarness.ResetAsync(typeof(SqlServerEndpointMetadataStoreTests));

    protected override IEndpointMetadataStore CreateStore()
        => SqlServerStoreTestHarness.CreateStore(typeof(SqlServerEndpointMetadataStoreTests));
}

[TestClass]
public sealed class SqlServerMetricsStoreTests : MetricsStoreConformanceTests
{
    [ClassInitialize]
    public static Task ClassInit(TestContext context)
        => SqlServerStoreTestHarness.InitializeAsync(typeof(SqlServerMetricsStoreTests));

    [TestInitialize]
    public Task ResetSchema()
        => SqlServerStoreTestHarness.ResetAsync(typeof(SqlServerMetricsStoreTests));

    protected override INimBusMessageStore CreateStore()
        => SqlServerStoreTestHarness.CreateStore(typeof(SqlServerMetricsStoreTests));
}
