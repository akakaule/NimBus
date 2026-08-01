#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.SDK;
using NimBus.SDK.Extensions;

namespace NimBus.SDK.Tests;

/// <summary>
/// Coverage for <see cref="IHandoffClientFactory"/> — the runtime per-endpoint
/// settlement-client factory used by processes that serve arbitrary endpoints
/// (e.g. the management WebApp), where the endpoint set is not known at
/// registration time and keyed singletons can't be pre-registered.
/// </summary>
[TestClass]
public class HandoffClientFactoryTests
{
    private static ServiceBusClient FakeClient() =>
        new("Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=AAA=");

    [TestMethod]
    public void ForEndpoint_returns_cached_instance_for_same_endpoint()
    {
        var factory = new HandoffClientFactory(FakeClient());

        var first = factory.ForEndpoint("EndpointA");
        var second = factory.ForEndpoint("EndpointA");

        Assert.AreSame(first, second,
            "Same endpoint must reuse the cached client (and its long-lived sender).");
    }

    [TestMethod]
    public async Task ForEndpoint_concurrent_first_use_creates_one_underlying_sender()
    {
        const int workerCount = 8;
        using var start = new Barrier(workerCount + 1);
        using var releaseCreateSender = new ManualResetEventSlim();
        var serviceBusClient = new BlockingServiceBusClient(releaseCreateSender);
        var factory = new HandoffClientFactory(serviceBusClient);

        var calls = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Factory.StartNew(
                () =>
                {
                    start.SignalAndWait();
                    return factory.ForEndpoint("EndpointA");
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        start.SignalAndWait();
        Assert.IsTrue(serviceBusClient.FirstCreateSenderEntered.Wait(TimeSpan.FromSeconds(5)),
            "The first caller never reached ServiceBusClient.CreateSender.");

        // Keep the first sender construction in flight briefly. A cache that stores
        // raw values lets ConcurrentDictionary invoke its value factory on the other
        // callers too; a Lazy cache lets only one caller create the underlying sender.
        await Task.Delay(200);
        releaseCreateSender.Set();
        var clients = await Task.WhenAll(calls);

        Assert.AreEqual(1, serviceBusClient.CreateSenderCallCount,
            "Concurrent first use must create exactly one long-lived ServiceBusSender.");
        Assert.IsTrue(clients.All(client => ReferenceEquals(clients[0], client)),
            "Every concurrent caller must receive the same cached IHandoffClient.");
    }

    [TestMethod]
    public void ForEndpoint_failed_first_creation_can_be_retried()
    {
        var serviceBusClient = new TransientFailureServiceBusClient();
        var factory = new HandoffClientFactory(serviceBusClient);

        Assert.ThrowsExactly<InvalidOperationException>(() => factory.ForEndpoint("EndpointA"));

        Assert.IsNotNull(factory.ForEndpoint("EndpointA"));
        Assert.AreEqual(2, serviceBusClient.CreateSenderCallCount,
            "A transient sender-creation failure must not poison the endpoint cache.");
    }

    [TestMethod]
    public void ForEndpoint_returns_distinct_instances_per_endpoint()
    {
        var factory = new HandoffClientFactory(FakeClient());

        var a = factory.ForEndpoint("EndpointA");
        var b = factory.ForEndpoint("EndpointB");

        Assert.AreNotSame(a, b,
            "Each endpoint must get its own client bound to its own topic sender.");
    }

    [TestMethod]
    public void ForEndpoint_with_null_or_empty_endpoint_throws()
    {
        var factory = new HandoffClientFactory(FakeClient());

        Assert.ThrowsExactly<ArgumentException>(() => factory.ForEndpoint(""));
        Assert.ThrowsExactly<ArgumentException>(() => factory.ForEndpoint(null!));
    }

    [TestMethod]
    public void AddNimBusHandoffClientFactory_resolves_and_is_idempotent()
    {
        var services = new ServiceCollection();
        services.AddSingleton(FakeClient());
        services.AddNimBusHandoffClientFactory();
        services.AddNimBusHandoffClientFactory();

        Assert.AreEqual(1, services.Count(d => d.ServiceType == typeof(IHandoffClientFactory)),
            "Registration must TryAdd — calling it twice must not stack descriptors.");

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHandoffClientFactory>();

        Assert.IsNotNull(factory.ForEndpoint("EndpointA"));
    }

    private sealed class BlockingServiceBusClient : ServiceBusClient
    {
        private readonly ManualResetEventSlim _releaseCreateSender;
        private int _createSenderCallCount;

        public BlockingServiceBusClient(ManualResetEventSlim releaseCreateSender)
        {
            _releaseCreateSender = releaseCreateSender;
        }

        public ManualResetEventSlim FirstCreateSenderEntered { get; } = new();

        public int CreateSenderCallCount => Volatile.Read(ref _createSenderCallCount);

        public override ServiceBusSender CreateSender(string queueOrTopicName)
        {
            var callNumber = Interlocked.Increment(ref _createSenderCallCount);
            if (callNumber == 1)
            {
                FirstCreateSenderEntered.Set();
                Assert.IsTrue(_releaseCreateSender.Wait(TimeSpan.FromSeconds(5)),
                    "The test did not release the blocked sender creation.");
            }

            return new StubServiceBusSender();
        }
    }

    private sealed class StubServiceBusSender : ServiceBusSender
    {
    }

    private sealed class TransientFailureServiceBusClient : ServiceBusClient
    {
        private int _createSenderCallCount;

        public int CreateSenderCallCount => Volatile.Read(ref _createSenderCallCount);

        public override ServiceBusSender CreateSender(string queueOrTopicName)
        {
            if (Interlocked.Increment(ref _createSenderCallCount) == 1)
                throw new InvalidOperationException("transient sender creation failure");

            return new StubServiceBusSender();
        }
    }
}
