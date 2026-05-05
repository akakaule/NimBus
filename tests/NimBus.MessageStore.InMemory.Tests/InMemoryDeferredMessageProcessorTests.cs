#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing;
using NimBus.Testing.Conformance;

namespace NimBus.MessageStore.InMemory.Tests;

[TestClass]
public sealed class InMemoryDeferredMessageProcessorTests : DeferredMessageProcessorConformanceTests
{
    // The InMemory parked store needs a reference to the *same* session-state
    // store instance so Increment/Decrement/Active-counter mutations stay in
    // sync. The conformance suite calls Create*Store() once per test method,
    // so we lazily memoise per-test using a fresh instance set on first call.
    private InMemorySessionStateStore? _sessionState;
    private InMemoryMessageStore? _tracking;

    [TestInitialize]
    public void Reset()
    {
        _sessionState = new InMemorySessionStateStore();
        _tracking = new InMemoryMessageStore();
    }

    protected override IParkedMessageStore CreateParkedStore()
        => new InMemoryParkedMessageStore(_sessionState!);

    protected override ISessionStateStore CreateSessionStateStore()
        => _sessionState!;

    protected override IMessageTrackingStore CreateTrackingStore()
        => _tracking!;
}
