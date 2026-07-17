#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.SDK;
using NimBus.SDK.EventHandlers;
using NimBus.Testing;
using NimBus.Testing.Extensions;

namespace NimBus.ServiceBus.Tests;

[TestClass]
public sealed class ScopedDynamicTestTransportTests
{
    [TestMethod]
    public async Task AddNimBusTestTransport_ScopedDynamicHandler_UsesAndDisposesDistinctMessageScopes()
    {
        var services = new ServiceCollection();
        var probes = new List<ScopedProbe>();
        services.AddScoped(_ =>
        {
            var probe = new ScopedProbe();
            probes.Add(probe);
            return probe;
        });
        services.AddNimBusTestTransport(builder =>
            builder.AddScopedDynamicHandler(
                nameof(ScopedDynamicEvent),
                provider => new ScopedDynamicHandler(provider.GetRequiredService<ScopedProbe>())));

        await using var root = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var publisher = root.GetRequiredService<IPublisherClient>();
        var bus = root.GetRequiredService<InMemoryMessageBus>();
        var messageHandler = root.GetRequiredService<IMessageHandler>();

        await publisher.Publish(new ScopedDynamicEvent());
        await publisher.Publish(new ScopedDynamicEvent());
        await bus.DeliverAll(messageHandler);

        Assert.AreEqual(2, probes.Count);
        Assert.AreNotSame(probes[0], probes[1]);
        Assert.IsTrue(probes.All(probe => probe.IsDisposed));
    }

    public sealed class ScopedDynamicEvent : Event;

    public sealed class ScopedDynamicHandler : IEventJsonHandler
    {
        private readonly ScopedProbe _probe;

        public ScopedDynamicHandler(ScopedProbe probe)
        {
            _probe = probe;
        }

        public Task Handle(IMessageContext context, CancellationToken cancellationToken = default)
        {
            Assert.IsFalse(_probe.IsDisposed);
            return Task.CompletedTask;
        }
    }

    public sealed class ScopedProbe : IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
