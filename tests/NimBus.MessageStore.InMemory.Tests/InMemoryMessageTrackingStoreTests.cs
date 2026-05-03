#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;

namespace NimBus.MessageStore.InMemory.Tests;

[TestClass]
public sealed class InMemoryMessageTrackingStoreTests : MessageTrackingStoreConformanceTests
{
    protected override IMessageTrackingStore CreateStore() => new InMemoryMessageStore();
}
