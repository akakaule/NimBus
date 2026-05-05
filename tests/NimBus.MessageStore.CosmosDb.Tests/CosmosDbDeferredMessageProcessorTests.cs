#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;

namespace NimBus.MessageStore.CosmosDb.Tests;

[TestClass]
public sealed class CosmosDbDeferredMessageProcessorTests : DeferredMessageProcessorConformanceTests
{
    // The Cosmos parked store needs the same CosmosDbSessionStateStore *type*
    // as its dependency (it calls internal helpers on the concrete class). We
    // create a fresh session-state store per test so its instance state stays
    // isolated, and pass that same instance to the parked store factory.
    private CosmosDbSessionStateStore? _sessionState;

    [TestInitialize]
    public void Reset()
    {
        _sessionState = CosmosDbStoreTestHarness.CreateSessionStateStore();
    }

    protected override IParkedMessageStore CreateParkedStore()
        => CosmosDbStoreTestHarness.CreateParkedStore(_sessionState!);

    protected override ISessionStateStore CreateSessionStateStore()
        => _sessionState!;

    protected override IMessageTrackingStore CreateTrackingStore()
        => CosmosDbStoreTestHarness.CreateStore();
}
