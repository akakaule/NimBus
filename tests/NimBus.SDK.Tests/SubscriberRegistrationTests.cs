#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Core.Outbox;
using NimBus.SDK;
using NimBus.SDK.EventHandlers;
using NimBus.SDK.Extensions;

namespace NimBus.SDK.Tests;

/// <summary>
/// Coverage for <see cref="ServiceCollectionExtensions.AddNimBusSubscriber"/>'s
/// one-endpoint-per-process invariant. <see cref="ISubscriberClient"/> is a
/// non-keyed singleton; a second registration against a different endpoint
/// used to be silently dropped by TryAddSingleton, binding the second
/// endpoint's handlers to a no-op. The guard now throws so the misuse is
/// loud at startup.
/// </summary>
[TestClass]
public class SubscriberRegistrationTests
{
    private const string FakeConnection =
        "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=AAA=";

    [TestMethod]
    public void Second_AddNimBusSubscriber_with_different_endpoint_throws()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddNimBusSubscriber("EndpointA", _ => { });

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            services.AddNimBusSubscriber("EndpointB", _ => { }));

        StringAssert.Contains(ex.Message, "EndpointA");
        StringAssert.Contains(ex.Message, "EndpointB");
    }

    [TestMethod]
    public void Second_AddNimBusSubscriber_with_same_endpoint_is_benign()
    {
        // Idempotent registration is harmless — TryAdd would keep the first
        // ISubscriberClient anyway. The guard only fires on a *different*
        // endpoint, where the silent-drop bug was real.
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddNimBusSubscriber("EndpointA", _ => { });
        services.AddNimBusSubscriber("EndpointA", _ => { });
    }

    [TestMethod]
    public void AddNimBusSubscriber_wires_registered_PermanentFailureClassifier_without_pipeline_or_lifecycle()
    {
        // Regression: a registered IPermanentFailureClassifier used to be silently
        // dropped when no MessagePipeline and no MessageLifecycleNotifier were
        // registered — the subscriber factory fell into a narrower StrictMessageHandler
        // ctor that never forwarded the classifier. Wire it unconditionally instead.
        var classifier = new SpyPermanentFailureClassifier();
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddSingleton<IPermanentFailureClassifier>(classifier);
        services.AddNimBusSubscriber("EndpointA", _ => { });

        using var provider = services.BuildServiceProvider();
        var subscriber = provider.GetRequiredService<ISubscriberClient>();

        // Observe the wiring through the object graph the factory builds:
        // SubscriberClient -> ServiceBusAdapter -> StrictMessageHandler.
        var adapter = GetPrivateField(subscriber, "_serviceBusAdapter");
        var handler = GetPrivateField(adapter, "_messageHandler");
        var wired = GetPrivateField(handler, "_permanentFailureClassifier");

        Assert.AreSame(classifier, wired,
            "A registered IPermanentFailureClassifier must be wired into StrictMessageHandler " +
            "even when no pipeline or lifecycle notifier is registered.");
    }

    [TestMethod]
    public async Task AddNimBusSubscriber_ScopedHandler_UsesPerMessageScopeWithValidateScopes()
    {
        var probes = new List<ScopedRegistrationProbe>();
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddScoped(_ =>
        {
            var probe = new ScopedRegistrationProbe();
            probes.Add(probe);
            return probe;
        });
        services.AddNimBusSubscriber(
            "EndpointA",
            builder => builder.AddHandler<ScopedRegistrationEvent, ScopedRegistrationHandler>());

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });
        var subscriber = provider.GetRequiredService<ISubscriberClient>();
        var eventHandlerProvider = (EventHandlerProvider)GetPrivateField(subscriber, "_eventHandlerProvider");

        await eventHandlerProvider.Handle(
            MessageContextStub.ForEventType(nameof(ScopedRegistrationEvent), "{}"));

        Assert.AreEqual(1, probes.Count);
        Assert.IsTrue(probes[0].IsDisposed);
    }

    [TestMethod]
    public void AddNimBusOutboxDispatcher_without_OutboxDispatcherSender_throws_actionable_message()
    {
        // The fail-fast message must name the real registration path.
        // OutboxDispatcherSender is NOT registered by AddNimBusPublisher — the
        // consumer registers it themselves as a singleton.
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddSingleton<IOutbox>(new NoopOutbox());
        services.AddNimBusOutboxDispatcher();

        using var provider = services.BuildServiceProvider();

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => provider.GetServices<IHostedService>().ToList());

        StringAssert.Contains(ex.Message, "OutboxDispatcherSender");
        Assert.IsFalse(
            ex.Message.Contains("Register AddNimBusPublisher before", StringComparison.Ordinal),
            "Message must not claim AddNimBusPublisher registers OutboxDispatcherSender.");
        StringAssert.Contains(ex.Message, "AddSingleton");
    }

    private static object GetPrivateField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, $"Expected private field '{fieldName}' on {target.GetType().Name}.");
        return field.GetValue(target);
    }

    private sealed class SpyPermanentFailureClassifier : IPermanentFailureClassifier
    {
        public bool IsPermanentFailure(Exception exception) => false;
    }

    public sealed class ScopedRegistrationEvent : Event
    {
    }

    public sealed class ScopedRegistrationHandler : IEventHandler<ScopedRegistrationEvent>
    {
        private readonly ScopedRegistrationProbe _probe;

        public ScopedRegistrationHandler(ScopedRegistrationProbe probe)
        {
            _probe = probe;
        }

        public Task Handle(
            ScopedRegistrationEvent message,
            IEventHandlerContext context,
            CancellationToken cancellationToken = default)
        {
            Assert.IsFalse(_probe.IsDisposed);
            return Task.CompletedTask;
        }
    }

    public sealed class ScopedRegistrationProbe : IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoopOutbox : IOutbox
    {
        public Task StoreAsync(OutboxMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OutboxMessage>>(Array.Empty<OutboxMessage>());
        public Task MarkAsDispatchedAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkAsDispatchedAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
