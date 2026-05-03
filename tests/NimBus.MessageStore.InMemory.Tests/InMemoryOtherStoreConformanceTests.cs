#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;

namespace NimBus.MessageStore.InMemory.Tests;

[TestClass]
public sealed class InMemorySubscriptionStoreTests : SubscriptionStoreConformanceTests
{
    protected override ISubscriptionStore CreateStore() => new InMemoryMessageStore();
}

[TestClass]
public sealed class InMemoryEndpointMetadataStoreTests : EndpointMetadataStoreConformanceTests
{
    protected override IEndpointMetadataStore CreateStore() => new InMemoryMessageStore();
}

[TestClass]
public sealed class InMemoryMetricsStoreTests : MetricsStoreConformanceTests
{
    protected override INimBusMessageStore CreateStore() => new InMemoryMessageStore();
}
