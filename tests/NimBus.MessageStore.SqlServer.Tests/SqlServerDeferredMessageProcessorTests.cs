#pragma warning disable CA1707, CA2007
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;

namespace NimBus.MessageStore.SqlServer.Tests;

[TestClass]
public sealed class SqlServerDeferredMessageProcessorTests : DeferredMessageProcessorConformanceTests
{
    [ClassInitialize]
    public static Task ClassInit(TestContext context)
        => SqlServerStoreTestHarness.InitializeAsync(typeof(SqlServerDeferredMessageProcessorTests));

    [TestInitialize]
    public Task ResetSchema()
        => SqlServerStoreTestHarness.ResetAsync(typeof(SqlServerDeferredMessageProcessorTests));

    protected override IParkedMessageStore CreateParkedStore()
        => SqlServerStoreTestHarness.CreateParkedStore(typeof(SqlServerDeferredMessageProcessorTests));

    protected override ISessionStateStore CreateSessionStateStore()
        => SqlServerStoreTestHarness.CreateSessionStateStore(typeof(SqlServerDeferredMessageProcessorTests));

    protected override IMessageTrackingStore CreateTrackingStore()
        => SqlServerStoreTestHarness.CreateStore(typeof(SqlServerDeferredMessageProcessorTests));
}
