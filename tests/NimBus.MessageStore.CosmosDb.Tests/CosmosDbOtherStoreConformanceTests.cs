#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;

namespace NimBus.MessageStore.CosmosDb.Tests;

[TestClass]
public sealed class CosmosDbSubscriptionStoreTests : SubscriptionStoreConformanceTests
{
    protected override ISubscriptionStore CreateStore()
        => CosmosDbStoreTestHarness.CreateStore();
}

[TestClass]
public sealed class CosmosDbEndpointMetadataStoreTests : EndpointMetadataStoreConformanceTests
{
    protected override IEndpointMetadataStore CreateStore()
        => CosmosDbStoreTestHarness.CreateStore();
}

[TestClass]
public sealed class CosmosDbMetricsStoreTests : MetricsStoreConformanceTests
{
    protected override INimBusMessageStore CreateStore()
        => CosmosDbStoreTestHarness.CreateStore();
}
