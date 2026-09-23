#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.SDK.Hosting;

namespace NimBus.SDK.Tests;

/// <summary>
/// Direct construction and lifecycle of <see cref="DeferredMessageProcessorHostedService"/>,
/// which is public so multi-endpoint hosts (the WebApp traffic simulator) can run one
/// instance per endpoint without going through DI.
/// </summary>
[TestClass]
public class DeferredMessageProcessorHostedServiceTests
{
    [TestMethod]
    public void Direct_construction_succeeds()
    {
        var service = Create(new RecordingClient(), new DeferredMessageProcessorHostedServiceOptions("orders", "deferredprocessor"));

        Assert.IsNotNull(service);
    }

    [TestMethod]
    public void Null_arguments_throw()
    {
        var client = new RecordingClient();
        var options = new DeferredMessageProcessorHostedServiceOptions("orders", "deferredprocessor");
        var processor = new NoopDeferredProcessor();
        var logger = NullLogger<DeferredMessageProcessorHostedService>.Instance;

        Assert.ThrowsExactly<ArgumentNullException>(() => new DeferredMessageProcessorHostedService(null!, processor, options, logger));
        Assert.ThrowsExactly<ArgumentNullException>(() => new DeferredMessageProcessorHostedService(client, null!, options, logger));
        Assert.ThrowsExactly<ArgumentNullException>(() => new DeferredMessageProcessorHostedService(client, processor, null!, logger));
        Assert.ThrowsExactly<ArgumentNullException>(() => new DeferredMessageProcessorHostedService(client, processor, options, null!));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    public void Blank_topic_throws(string topic)
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            Create(new RecordingClient(), new DeferredMessageProcessorHostedServiceOptions(topic, "deferredprocessor")));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    public void Blank_subscription_throws(string subscription)
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            Create(new RecordingClient(), new DeferredMessageProcessorHostedServiceOptions("orders", subscription)));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void Non_positive_concurrency_throws(int maxConcurrentCalls)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            Create(new RecordingClient(), new DeferredMessageProcessorHostedServiceOptions("orders", "deferredprocessor", maxConcurrentCalls)));
    }

    [TestMethod]
    public async Task Start_creates_processor_for_topic_and_subscription_and_stop_disposes_it()
    {
        var client = new RecordingClient();
        var service = Create(client, new DeferredMessageProcessorHostedServiceOptions("orders", "deferredprocessor", 3));

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => client.Processors.Count == 1 && client.Processors[0].StartCalls == 1);

        Assert.AreEqual("orders", client.TopicName);
        Assert.AreEqual("deferredprocessor", client.SubscriptionName);
        Assert.IsFalse(client.Options!.AutoCompleteMessages);
        Assert.AreEqual(3, client.Options.MaxConcurrentCalls);

        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(1, client.Processors[0].StopCalls);
        Assert.IsTrue(client.Processors[0].Disposed);
    }

    [TestMethod]
    public async Task Stop_with_cancelled_token_returns_promptly()
    {
        var client = new RecordingClient();
        var service = Create(client, new DeferredMessageProcessorHostedServiceOptions("orders", "deferredprocessor"));
        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => client.Processors.Count == 1);
        client.Processors[0].BlockStop();

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var stop = service.StopAsync(cancelled.Token);
        var completed = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.AreSame(stop, completed, "StopAsync with a cancelled token must not wait for a stalled processor stop.");
        client.Processors[0].ReleaseStop();
    }

    [TestMethod]
    public async Task Start_stop_start_on_a_fresh_instance_creates_a_new_processor()
    {
        var client = new RecordingClient();
        var options = new DeferredMessageProcessorHostedServiceOptions("orders", "deferredprocessor");

        var first = Create(client, options);
        await first.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => client.Processors.Count == 1);
        await first.StopAsync(CancellationToken.None);

        var second = Create(client, options);
        await second.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => client.Processors.Count == 2 && client.Processors[1].StartCalls == 1);
        await second.StopAsync(CancellationToken.None);

        Assert.IsTrue(client.Processors[0].Disposed);
        Assert.IsTrue(client.Processors[1].Disposed);
    }

    private static DeferredMessageProcessorHostedService Create(ServiceBusClient client, DeferredMessageProcessorHostedServiceOptions options) =>
        new(client, new NoopDeferredProcessor(), options, NullLogger<DeferredMessageProcessorHostedService>.Instance);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            if (cts.IsCancellationRequested)
            {
                Assert.Fail("Timed out waiting for condition.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class NoopDeferredProcessor : IDeferredMessageProcessor
    {
        public Task ProcessDeferredMessagesAsync(string sessionId, string topicName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingClient : ServiceBusClient
    {
        public RecordingClient()
            : base("Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=ZmFrZQ==")
        {
        }

        public List<RecordingProcessor> Processors { get; } = new();
        public string? TopicName { get; private set; }
        public string? SubscriptionName { get; private set; }
        public ServiceBusProcessorOptions? Options { get; private set; }

        public override ServiceBusProcessor CreateProcessor(string topicName, string subscriptionName, ServiceBusProcessorOptions options)
        {
            TopicName = topicName;
            SubscriptionName = subscriptionName;
            Options = options;
            var processor = new RecordingProcessor(this, topicName, subscriptionName, options);
            Processors.Add(processor);
            return processor;
        }
    }

    private sealed class RecordingProcessor : ServiceBusProcessor
    {
        private TaskCompletionSource? _stopCompletion;

        public RecordingProcessor(ServiceBusClient client, string topicName, string subscriptionName, ServiceBusProcessorOptions options)
            : base(client, topicName, subscriptionName, options)
        {
        }

        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public bool Disposed => IsClosed;

        public void BlockStop() => _stopCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseStop() => _stopCompletion?.TrySetResult();

        public override Task StartProcessingAsync(CancellationToken cancellationToken = default)
        {
            StartCalls++;
            return Task.CompletedTask;
        }

        public override Task StopProcessingAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            return _stopCompletion?.Task ?? Task.CompletedTask;
        }
    }
}
