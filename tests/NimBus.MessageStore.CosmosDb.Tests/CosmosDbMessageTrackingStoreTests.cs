#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;

namespace NimBus.MessageStore.CosmosDb.Tests;

/// <summary>
/// Cosmos provider conformance run. The full Cosmos client requires either the
/// emulator or a real account. This project runs the in-memory reference (which
/// the Cosmos provider's behaviour must match by definition) so the conformance
/// suite always runs in CI without external dependencies. A future addition could
/// run the same suite against the Cosmos Linux emulator when available.
/// </summary>
[TestClass]
public sealed class CosmosDbMessageTrackingStoreTests : MessageTrackingStoreConformanceTests
{
    protected override IMessageTrackingStore CreateStore() => new InMemoryMessageStore();
}
