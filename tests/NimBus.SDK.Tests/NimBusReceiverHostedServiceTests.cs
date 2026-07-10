#pragma warning disable CA1707
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.SDK.Hosting;
using NimBus.ServiceBus;

namespace NimBus.SDK.Tests
{
    [TestClass]
    public class NimBusReceiverHostedServiceTests
    {
        [TestMethod]
        public async Task RecoverableProcessorErrors_RestartSessionProcessor()
        {
            var client = new RecordingServiceBusClient();
            var service = new TestableNimBusReceiverHostedService(
                client,
                new NoopServiceBusAdapter(),
                new NimBusReceiverOptions
                {
                    TopicName = "orders",
                    SubscriptionName = "orders",
                    RecoverableErrorRestartThreshold = 2,
                    RecoverableErrorDelay = TimeSpan.Zero,
                    ProcessorRestartDelay = TimeSpan.Zero,
                },
                NullLogger<NimBusReceiverHostedService>.Instance);

            using var cts = new CancellationTokenSource();
            var runTask = service.RunProcessorLoopAsync(cts.Token);

            try
            {
                await WaitUntilAsync(() => client.Processors.Count == 1, () => $"ProcessorCount={client.Processors.Count}");
                Assert.AreEqual(1, client.Processors[0].StartCalls);

                await service.HandleProcessorErrorAsync(CreateErrorArgs(
                    new ObjectDisposedException("connection"),
                    ServiceBusErrorSource.AcceptSession));
                await service.HandleProcessorErrorAsync(CreateErrorArgs(
                    new ObjectDisposedException("connection"),
                    ServiceBusErrorSource.AcceptSession));

                await WaitUntilAsync(() => client.Processors.Count == 2, () => $"ProcessorCount={client.Processors.Count}");

                Assert.AreEqual(1, client.Processors[0].StopCalls);
                Assert.AreEqual(1, client.Processors[1].StartCalls);
            }
            finally
            {
                await StopServiceAsync(cts, runTask);
            }
        }

        [TestMethod]
        public async Task ProcessMessageCallbackErrors_DoNotRestartSessionProcessor()
        {
            var client = new RecordingServiceBusClient();
            var service = new TestableNimBusReceiverHostedService(
                client,
                new NoopServiceBusAdapter(),
                new NimBusReceiverOptions
                {
                    TopicName = "orders",
                    SubscriptionName = "orders",
                    RecoverableErrorRestartThreshold = 1,
                    RecoverableErrorDelay = TimeSpan.Zero,
                    ProcessorRestartDelay = TimeSpan.Zero,
                },
                NullLogger<NimBusReceiverHostedService>.Instance);

            using var cts = new CancellationTokenSource();
            var runTask = service.RunProcessorLoopAsync(cts.Token);

            try
            {
                await WaitUntilAsync(() => client.Processors.Count == 1, () => $"ProcessorCount={client.Processors.Count}");

                await service.HandleProcessorErrorAsync(CreateErrorArgs(
                    new InvalidOperationException("handler failed"),
                    ServiceBusErrorSource.ProcessMessageCallback));

                await Task.Delay(100);

                Assert.AreEqual(1, client.Processors.Count);
            }
            finally
            {
                await StopServiceAsync(cts, runTask);
            }
        }

        [TestMethod]
        public async Task ReceiverOptions_AreAppliedToSessionProcessor()
        {
            var client = new RecordingServiceBusClient();
            var service = new TestableNimBusReceiverHostedService(
                client,
                new NoopServiceBusAdapter(),
                new NimBusReceiverOptions
                {
                    TopicName = "orders",
                    SubscriptionName = "orders",
                    MaxConcurrentSessions = 17,
                    MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(9),
                    SessionIdleTimeout = TimeSpan.FromSeconds(42),
                },
                NullLogger<NimBusReceiverHostedService>.Instance);

            using var cts = new CancellationTokenSource();
            var runTask = service.RunProcessorLoopAsync(cts.Token);

            try
            {
                await WaitUntilAsync(() => client.Processors.Count == 1, () => $"ProcessorCount={client.Processors.Count}");

                Assert.AreEqual("orders", client.TopicName);
                Assert.AreEqual("orders", client.SubscriptionName);
                Assert.AreEqual(17, client.Options.MaxConcurrentSessions);
                Assert.AreEqual(TimeSpan.FromMinutes(9), client.Options.MaxAutoLockRenewalDuration);
                Assert.AreEqual(TimeSpan.FromSeconds(42), client.Options.SessionIdleTimeout);
                Assert.IsFalse(client.Options.AutoCompleteMessages);
            }
            finally
            {
                await StopServiceAsync(cts, runTask);
            }
        }

        [TestMethod]
        public async Task RecoveryRestart_WithRealProcessorState_DoesNotFaultTheLoop()
        {
            // Regression: StopAndDisposeProcessorAsync used to detach the event handlers
            // BEFORE StopProcessingAsync. The Azure SDK forbids removing handlers from a
            // running processor (EnsureNotRunningAndInvoke throws InvalidOperationException),
            // so the recovery-restart path — the one place designed to keep the receiver
            // alive — crashed the host instead. The doubles in the other tests no-op
            // Start/Stop, which hides the guard; this test keeps the REAL base
            // start/stop semantics (IsProcessing state) on a loopback endpoint.
            var client = new RealStateServiceBusClient();
            var service = new TestableNimBusReceiverHostedService(
                client,
                new NoopServiceBusAdapter(),
                new NimBusReceiverOptions
                {
                    TopicName = "orders",
                    SubscriptionName = "orders",
                    RecoverableErrorRestartThreshold = 2,
                    RecoverableErrorDelay = TimeSpan.Zero,
                    ProcessorRestartDelay = TimeSpan.Zero,
                },
                NullLogger<NimBusReceiverHostedService>.Instance);

            using var cts = new CancellationTokenSource();
            var runTask = service.RunProcessorLoopAsync(cts.Token);

            try
            {
                await WaitUntilAsync(() => client.Processors.Count == 1, () => $"ProcessorCount={client.Processors.Count}");
                await WaitUntilAsync(() => client.Processors[0].IsProcessing, () => "Processor not started");

                await service.HandleProcessorErrorAsync(CreateErrorArgs(
                    new ObjectDisposedException("connection"),
                    ServiceBusErrorSource.AcceptSession));
                await service.HandleProcessorErrorAsync(CreateErrorArgs(
                    new ObjectDisposedException("connection"),
                    ServiceBusErrorSource.AcceptSession));

                // With the buggy detach-before-stop order the loop task faults with
                // InvalidOperationException here instead of creating processor #2.
                await WaitUntilAsync(
                    () => client.Processors.Count >= 2 || runTask.IsFaulted,
                    () => $"ProcessorCount={client.Processors.Count}, Faulted={runTask.IsFaulted}");

                Assert.IsFalse(runTask.IsFaulted, $"Receiver loop faulted instead of restarting: {runTask.Exception?.GetBaseException().Message}");
                Assert.IsTrue(client.Processors.Count >= 2, "A replacement processor must be created after the restart request");
            }
            finally
            {
                await StopServiceAsync(cts, runTask);
            }
        }

        private static async Task StopServiceAsync(CancellationTokenSource cts, Task runTask)
        {
            cts.Cancel();
            var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(5)));
            if (completed != runTask)
            {
                Assert.Fail("Timed out waiting for receiver loop to stop.");
            }

            await runTask;
        }

        private static async Task WaitUntilAsync(Func<bool> predicate, Func<string> describeState)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!predicate())
            {
                if (cts.IsCancellationRequested)
                {
                    Assert.Fail($"Timed out waiting for condition. {describeState()}");
                }

                await Task.Delay(10);
            }
        }

        private static ProcessErrorEventArgs CreateErrorArgs(Exception exception, ServiceBusErrorSource errorSource) =>
            new ProcessErrorEventArgs(
                exception,
                errorSource,
                "test.servicebus.windows.net",
                "orders/subscriptions/orders",
                "test-processor",
                CancellationToken.None);

        private sealed class RecordingServiceBusClient : ServiceBusClient
        {
            public RecordingServiceBusClient()
                : base("Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=ZmFrZQ==")
            {
            }

            public List<RecordingServiceBusSessionProcessor> Processors { get; } = new List<RecordingServiceBusSessionProcessor>();
            public string TopicName { get; private set; } = string.Empty;
            public string SubscriptionName { get; private set; } = string.Empty;
            public ServiceBusSessionProcessorOptions Options { get; private set; } = new ServiceBusSessionProcessorOptions();

            public override ServiceBusSessionProcessor CreateSessionProcessor(
                string topicName,
                string subscriptionName,
                ServiceBusSessionProcessorOptions options)
            {
                TopicName = topicName;
                SubscriptionName = subscriptionName;
                Options = options;

                var processor = new RecordingServiceBusSessionProcessor(this, topicName, subscriptionName, options);
                Processors.Add(processor);
                return processor;
            }
        }

        // Client whose processors keep the REAL ServiceBusSessionProcessor start/stop
        // behavior (IsProcessing state and the running-processor handler-removal guard).
        // Points at loopback so the background receive tasks fail fast without leaving
        // the machine; those connection errors only surface through ProcessErrorAsync.
        private sealed class RealStateServiceBusClient : ServiceBusClient
        {
            public RealStateServiceBusClient()
                : base("Endpoint=sb://127.0.0.1;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=ZmFrZQ==")
            {
            }

            public List<ServiceBusSessionProcessor> Processors { get; } = new List<ServiceBusSessionProcessor>();

            public override ServiceBusSessionProcessor CreateSessionProcessor(
                string topicName,
                string subscriptionName,
                ServiceBusSessionProcessorOptions options)
            {
                var processor = base.CreateSessionProcessor(topicName, subscriptionName, options);
                Processors.Add(processor);
                return processor;
            }
        }

        private sealed class TestableNimBusReceiverHostedService : NimBusReceiverHostedService
        {
            public TestableNimBusReceiverHostedService(
                ServiceBusClient client,
                IServiceBusAdapter adapter,
                NimBusReceiverOptions options,
                NullLogger<NimBusReceiverHostedService> logger)
                : base(client, adapter, options, logger)
            {
            }

            protected override ValueTask DisposeProcessorAsync(ServiceBusSessionProcessor processor) =>
                default;
        }

        private sealed class RecordingServiceBusSessionProcessor : ServiceBusSessionProcessor
        {
            public RecordingServiceBusSessionProcessor(
                ServiceBusClient client,
                string topicName,
                string subscriptionName,
                ServiceBusSessionProcessorOptions options)
                : base(client, topicName, subscriptionName, options)
            {
            }

            public int StartCalls { get; private set; }
            public int StopCalls { get; private set; }

            public override Task StartProcessingAsync(CancellationToken cancellationToken = default)
            {
                StartCalls++;
                return Task.CompletedTask;
            }

            public override Task StopProcessingAsync(CancellationToken cancellationToken = default)
            {
                StopCalls++;
                return Task.CompletedTask;
            }

        }

        private sealed class NoopServiceBusAdapter : IServiceBusAdapter
        {
            public Task Handle(ServiceBusReceivedMessage message, ServiceBusSessionMessageActions sessionActions, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task Handle(ServiceBusReceivedMessage message, ServiceBusMessageActions messageActions, ServiceBusSessionMessageActions sessionActions, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task Handle(ServiceBusReceivedMessage message, ServiceBusSessionReceiver sessionReceiver, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task Handle(ProcessSessionMessageEventArgs args, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }
    }
}
