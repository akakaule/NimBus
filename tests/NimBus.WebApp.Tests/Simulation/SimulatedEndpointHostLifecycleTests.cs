#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Services.Simulation;

namespace NimBus.WebApp.Tests.Simulation;

/// <summary>
/// Lifecycle of the real <see cref="SimulatedEndpointHost"/> over a recording
/// <see cref="ServiceBusClient"/>: the processors it creates, and that stop is bounded.
/// </summary>
[TestClass]
public sealed class SimulatedEndpointHostLifecycleTests
{
    [TestMethod]
    public async Task Start_creates_the_session_receiver_and_the_deferred_processor_for_the_endpoint()
    {
        var client = new RecordingClient();
        var host = CreateHost(client);

        await host.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => client.SessionProcessors.Count == 1 && client.Processors.Count == 1);

        var session = client.SessionProcessors.Single();
        Assert.AreEqual((SimulationTestPlatform.Billing, SimulationTestPlatform.Billing), (session.Topic, session.Subscription));
        Assert.IsFalse(session.Options.AutoCompleteMessages);

        var deferred = client.Processors.Single();
        Assert.AreEqual((SimulationTestPlatform.Billing, SimulatedEndpointHost.DeferredProcessorSubscription), (deferred.Topic, deferred.Subscription));
        Assert.AreEqual(1, deferred.Options.MaxConcurrentCalls, "Single concurrency is the deferred replay's only ordering mechanism.");

        await host.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task Stop_stops_and_disposes_both_processors_and_cancels_in_flight_handlers()
    {
        var client = new RecordingClient();
        var host = CreateHost(client);
        await host.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => client.SessionProcessors.Count == 1 && client.Processors.Count == 1);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await host.StopAsync(deadline.Token);

        Assert.IsTrue(host.StoppingToken.IsCancellationRequested, "The stopping token is linked into every delivery.");
        await WaitUntilAsync(() => client.SessionProcessors[0].StopCalls >= 1 && client.Processors[0].StopCalls >= 1);
        await WaitUntilAsync(() => client.SessionProcessors[0].IsClosed && client.Processors[0].IsClosed);
    }

    [TestMethod]
    public async Task Stop_returns_at_its_deadline_when_a_processor_stop_stalls()
    {
        var client = new RecordingClient { StallStops = true };
        var host = CreateHost(client);
        await host.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => client.SessionProcessors.Count == 1 && client.Processors.Count == 1);

        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var stop = host.StopAsync(deadline.Token);
        var completed = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.AreSame(stop, completed, "StopAsync must honour its deadline even when the processors never stop.");
        client.ReleaseStops();
    }

    [TestMethod]
    public async Task Drain_returns_once_the_handler_is_idle()
    {
        var client = new RecordingClient();
        var host = CreateHost(client);
        await host.StartAsync(CancellationToken.None);

        var drain = host.DrainAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        var completed = await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.AreSame(drain, completed, "With nothing in flight the drain must not wait out its window.");
        await host.StopAsync(CancellationToken.None);
    }

    private static SimulatedEndpointHost CreateHost(ServiceBusClient client) =>
        new(
            client,
            SimulationTestPlatform.Billing,
            new[] { "OrderPlaced" },
            new SimulatedHandlerBehavior(SimulationTestPlatform.Billing, new RecordingFeedSink()),
            NullLoggerFactory.Instance);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            if (timeout.IsCancellationRequested)
                Assert.Fail("Timed out waiting for condition.");
            await Task.Delay(10);
        }
    }

    private sealed class RecordingClient : ServiceBusClient
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingClient()
            : base("Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=ZmFrZQ==")
        {
        }

        public bool StallStops { get; init; }

        public List<RecordingSessionProcessor> SessionProcessors { get; } = new();

        public List<RecordingProcessor> Processors { get; } = new();

        public Task StopGate => StallStops ? _release.Task : Task.CompletedTask;

        public void ReleaseStops() => _release.TrySetResult();

        public override ServiceBusSessionProcessor CreateSessionProcessor(string topicName, string subscriptionName, ServiceBusSessionProcessorOptions options)
        {
            var processor = new RecordingSessionProcessor(this, topicName, subscriptionName, options);
            SessionProcessors.Add(processor);
            return processor;
        }

        public override ServiceBusProcessor CreateProcessor(string topicName, string subscriptionName, ServiceBusProcessorOptions options)
        {
            var processor = new RecordingProcessor(this, topicName, subscriptionName, options);
            Processors.Add(processor);
            return processor;
        }
    }

    private sealed class RecordingSessionProcessor(RecordingClient client, string topic, string subscription, ServiceBusSessionProcessorOptions options)
        : ServiceBusSessionProcessor(client, topic, subscription, options)
    {
        public string Topic { get; } = topic;
        public string Subscription { get; } = subscription;
        public ServiceBusSessionProcessorOptions Options { get; } = options;
        public int StopCalls { get; private set; }

        public override Task StartProcessingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task StopProcessingAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            return client.StopGate;
        }
    }

    private sealed class RecordingProcessor(RecordingClient client, string topic, string subscription, ServiceBusProcessorOptions options)
        : ServiceBusProcessor(client, topic, subscription, options)
    {
        public string Topic { get; } = topic;
        public string Subscription { get; } = subscription;
        public ServiceBusProcessorOptions Options { get; } = options;
        public int StopCalls { get; private set; }

        public override Task StartProcessingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task StopProcessingAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            return client.StopGate;
        }
    }
}
