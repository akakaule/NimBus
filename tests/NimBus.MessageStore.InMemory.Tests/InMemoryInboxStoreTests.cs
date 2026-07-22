#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Inbox;
using NimBus.Testing;
using NimBus.Testing.Conformance;

namespace NimBus.MessageStore.InMemory.Tests;

[TestClass]
public sealed class InMemoryInboxStoreConformanceTests : InboxStoreConformanceTests
{
    private ManualTimeProvider _timeProvider = null!;

    protected override Task<IInboxStore> CreateStoreAsync()
    {
        _timeProvider = new ManualTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return Task.FromResult<IInboxStore>(new InMemoryInboxStore(_timeProvider));
    }

    protected override Task<DateTimeOffset> AdvancePastFirstRecordAsync()
    {
        _timeProvider.Advance(TimeSpan.FromMinutes(1));
        return Task.FromResult(_timeProvider.GetUtcNow());
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }
}

[TestClass]
public sealed class InMemoryInboxStoreTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1_001)]
    public void Constructor_rejects_unbounded_purge_batch_sizes(int batchSize)
    {
        var exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new InMemoryInboxStore(TimeProvider.System, batchSize));

        Assert.AreEqual("purgeBatchSize", exception.ParamName);
    }

    [TestMethod]
    public async Task PurgeExpiredAsync_is_bounded()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryInboxStore(timeProvider);

        for (var index = 0; index < 1_001; index++)
        {
            await store.RecordProcessedAsync("billing", $"message-{index}");
        }

        timeProvider.Advance(TimeSpan.FromMinutes(1));

        Assert.AreEqual(1_000, await store.PurgeExpiredAsync("billing", timeProvider.GetUtcNow()));
        Assert.AreEqual(1, await store.PurgeExpiredAsync("billing", timeProvider.GetUtcNow()));
    }

    [TestMethod]
    public void AddNimBusInMemoryInbox_registers_the_same_keyed_and_unkeyed_singleton()
    {
        var services = new ServiceCollection();

        services.AddNimBusInMemoryInbox();

        using var provider = services.BuildServiceProvider();
        var unkeyed = provider.GetRequiredService<IInboxStore>();
        var keyed = provider.GetRequiredKeyedService<IInboxStore>(InboxStore.InMemory);

        Assert.AreSame(unkeyed, keyed);
        Assert.IsInstanceOfType<InMemoryInboxStore>(unkeyed);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }
}
