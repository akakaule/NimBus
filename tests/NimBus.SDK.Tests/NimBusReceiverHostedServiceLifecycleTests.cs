#pragma warning disable CA1707
using System;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.SDK.Hosting;
using NimBus.ServiceBus;

namespace NimBus.SDK.Tests;

/// <summary>
/// Host lifecycle, error classification and option validation of the receiver: startup
/// failures must fail the host, only infrastructure errors may restart the processor, and
/// back-off and shutdown must stay bounded. Shares the fakes of the main test class.
/// </summary>
public partial class NimBusReceiverHostedServiceTests
{
    [TestMethod]
    public async Task StartAsync_WhenTheProcessorCannotStart_FaultsHostStartup()
    {
        // A missing entity or a rejected credential must stop the host from starting, not
        // leave it "running" with a receiver that never receives.
        var client = new RecordingServiceBusClient { StartException = new UnauthorizedAccessException("listen claim missing") };
        var service = CreateService(client, new NimBusReceiverOptions { TopicName = "orders", SubscriptionName = "orders" });

        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => service.StartAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task StartAndStop_StartsThenStopsAndDisposesTheProcessor()
    {
        var client = new RecordingServiceBusClient();
        var service = CreateService(client, new NimBusReceiverOptions { TopicName = "orders", SubscriptionName = "orders" });

        await service.StartAsync(CancellationToken.None);
        Assert.AreEqual(1, client.Processors[0].StartCalls, "StartAsync returns only once the processor is receiving.");

        await service.StopAsync(CancellationToken.None);

        Assert.IsTrue(client.Processors[0].StopCalls >= 1);
        Assert.IsTrue(service.DisposeCalls >= 1);
    }

    [TestMethod]
    public async Task ReceivedMessage_IsHandedToTheAdapter_AndResetsTheRecoverableErrorCount()
    {
        // A successfully handled message proves the connection works again, so an earlier
        // recoverable error must not count toward the next restart.
        var client = new RecordingServiceBusClient();
        var adapter = new RecordingServiceBusAdapter();
        var service = new TestableNimBusReceiverHostedService(
            client,
            adapter,
            RestartingOptions(threshold: 2),
            NullLogger<NimBusReceiverHostedService>.Instance);

        using var cts = new CancellationTokenSource();
        var runTask = service.RunProcessorLoopAsync(cts.Token);

        try
        {
            await WaitUntilAsync(() => client.Processors.Count == 1, () => $"ProcessorCount={client.Processors.Count}");

            await service.HandleProcessorErrorAsync(CreateErrorArgs(new ObjectDisposedException("connection"), ServiceBusErrorSource.Receive));
            await client.Processors[0].RaiseMessageAsync();
            await service.HandleProcessorErrorAsync(CreateErrorArgs(new ObjectDisposedException("connection"), ServiceBusErrorSource.Receive));

            await Task.Delay(100);

            Assert.AreEqual(1, adapter.HandledMessages);
            Assert.AreEqual(1, client.Processors.Count, "Two errors separated by a success must not restart the processor.");
        }
        finally
        {
            await StopServiceAsync(cts, runTask);
        }
    }

    [TestMethod]
    [DataRow("socket")]
    [DataRow("websocket")]
    [DataRow("io")]
    [DataRow("service-busy")]
    [DataRow("service-timeout")]
    [DataRow("communication")]
    [DataRow("general")]
    public async Task RecoverableInfrastructureError_RestartsTheProcessor(string kind)
    {
        var exception = kind switch
        {
            // Transport failures arrive wrapped; the inner-exception chain is searched.
            "socket" => new InvalidOperationException("receive failed", new SocketException()),
            "websocket" => new InvalidOperationException("receive failed", new WebSocketException()),
            "io" => new InvalidOperationException("receive failed", new InvalidOperationException("inner", new IOException("pipe"))),
            "service-busy" => new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy),
            "service-timeout" => new ServiceBusException("timeout", ServiceBusFailureReason.ServiceTimeout),
            "communication" => new ServiceBusException("comm", ServiceBusFailureReason.ServiceCommunicationProblem),
            _ => (Exception)new ServiceBusException("general", ServiceBusFailureReason.GeneralError),
        };

        Assert.AreEqual(2, await ProcessorCountAfterOneError(exception), $"{kind} is a recoverable infrastructure error.");
    }

    [TestMethod]
    [DataRow("entity-not-found")]
    [DataRow("unauthorized")]
    [DataRow("plain")]
    public async Task NonRecoverableError_DoesNotRestartTheProcessor(string kind)
    {
        // Restarting cannot fix a missing entity or a bad credential; it would only spin.
        var exception = kind switch
        {
            "entity-not-found" => new ServiceBusException("gone", ServiceBusFailureReason.MessagingEntityNotFound),
            "unauthorized" => new UnauthorizedAccessException("no listen claim"),
            _ => (Exception)new InvalidOperationException("unrelated"),
        };

        Assert.AreEqual(1, await ProcessorCountAfterOneError(exception));
    }

    [TestMethod]
    public async Task RecoverableErrorDelay_IsCappedAtTheMaximum()
    {
        var service = CreateService(new RecordingServiceBusClient(), new NimBusReceiverOptions
        {
            TopicName = "orders",
            SubscriptionName = "orders",
            RecoverableErrorDelay = TimeSpan.FromHours(1),
            MaxRecoverableErrorDelay = TimeSpan.Zero,
            RecoverableErrorRestartThreshold = 100,
        });

        var handling = service.HandleProcessorErrorAsync(CreateErrorArgs(new ObjectDisposedException("connection"), ServiceBusErrorSource.Receive));

        Assert.AreSame(handling, await Task.WhenAny(handling, Task.Delay(TimeSpan.FromSeconds(5))),
            "A zero maximum must cap the hour-long base delay.");
    }

    [TestMethod]
    public async Task RecoverableErrorDelay_EndsQuietlyWhenTheProcessorStops()
    {
        var service = CreateService(new RecordingServiceBusClient(), new NimBusReceiverOptions
        {
            TopicName = "orders",
            SubscriptionName = "orders",
            RecoverableErrorDelay = TimeSpan.FromHours(1),
            MaxRecoverableErrorDelay = TimeSpan.FromHours(1),
            RecoverableErrorRestartThreshold = 100,
        });
        using var stopping = new CancellationTokenSource();
        var args = new ProcessErrorEventArgs(
            new ObjectDisposedException("connection"),
            ServiceBusErrorSource.Receive,
            "test.servicebus.windows.net",
            "orders/subscriptions/orders",
            "test-processor",
            stopping.Token);

        var handling = service.HandleProcessorErrorAsync(args);
        await stopping.CancelAsync();

        Assert.AreSame(handling, await Task.WhenAny(handling, Task.Delay(TimeSpan.FromSeconds(5))));
        await handling; // The cancelled back-off is swallowed, not thrown into the processor.
    }

    [TestMethod]
    public async Task ProcessorRestartDelay_IsWaitedBeforeTheReplacementStarts()
    {
        var client = new RecordingServiceBusClient();
        var restartDelay = TimeSpan.FromMilliseconds(300);
        var options = RestartingOptions(threshold: 1);
        options.ProcessorRestartDelay = restartDelay;
        var service = CreateService(client, options);

        using var cts = new CancellationTokenSource();
        var runTask = service.RunProcessorLoopAsync(cts.Token);

        try
        {
            await WaitUntilAsync(() => client.Processors.Count == 1, () => $"ProcessorCount={client.Processors.Count}");
            var stopwatch = Stopwatch.StartNew();

            await service.HandleProcessorErrorAsync(CreateErrorArgs(new ObjectDisposedException("connection"), ServiceBusErrorSource.Receive));
            await WaitUntilAsync(() => client.Processors.Count == 2, () => $"ProcessorCount={client.Processors.Count}");

            Assert.IsTrue(stopwatch.Elapsed >= restartDelay - TimeSpan.FromMilliseconds(20),
                $"The replacement started after {stopwatch.Elapsed.TotalMilliseconds} ms, before the {restartDelay.TotalMilliseconds} ms restart delay.");
        }
        finally
        {
            await cts.CancelAsync();
            await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(5)));
        }
    }

    [TestMethod]
    public async Task ProcessorStopFailure_IsLoggedAndTheProcessorIsStillDisposed()
    {
        var client = new RecordingServiceBusClient();
        var logger = new RecordingLogger();
        var service = new TestableNimBusReceiverHostedService(
            client,
            new NoopServiceBusAdapter(),
            new NimBusReceiverOptions { TopicName = "orders", SubscriptionName = "orders" },
            logger);

        using var cts = new CancellationTokenSource();
        var runTask = service.RunProcessorLoopAsync(cts.Token);

        try
        {
            await WaitUntilAsync(() => client.Processors.Count == 1, () => $"ProcessorCount={client.Processors.Count}");
            client.Processors[0].StopException = new ServiceBusException("link detached", ServiceBusFailureReason.ServiceCommunicationProblem);

            await service.StopAndDisposeProcessorAsync(CancellationToken.None);

            Assert.AreEqual(1, logger.WarningCalls);
            Assert.IsInstanceOfType<ServiceBusException>(logger.LastWarningException);
            Assert.AreEqual(1, service.DisposeCalls);
        }
        finally
        {
            await StopServiceAsync(cts, runTask);
        }
    }

    [TestMethod]
    public async Task CancelledStopWithFailedDisposal_KeepsCancellationAsTheOutcome()
    {
        var client = new RecordingServiceBusClient();
        var logger = new RecordingLogger();
        var service = new TestableNimBusReceiverHostedService(
            client,
            new NoopServiceBusAdapter(),
            new NimBusReceiverOptions { TopicName = "orders", SubscriptionName = "orders" },
            logger);

        using var runCancellation = new CancellationTokenSource();
        var runTask = service.RunProcessorLoopAsync(runCancellation.Token);

        try
        {
            await WaitUntilAsync(() => client.Processors.Count == 1, () => $"ProcessorCount={client.Processors.Count}");
            client.Processors[0].CancelStop = true;
            service.FailNextDisposal(new InvalidOperationException("dispose failed"));
            using var stopCancellation = new CancellationTokenSource();
            await stopCancellation.CancelAsync();

            await Assert.ThrowsExactlyAsync<TaskCanceledException>(
                () => service.StopAndDisposeProcessorAsync(stopCancellation.Token));

            // The disposal failure is logged, not thrown over the cancellation. (The cancelled
            // stop task is observed and logged in the background as well.)
            Assert.IsTrue(
                logger.WarningExceptions.Any(e => e is InvalidOperationException { Message: "dispose failed" }),
                "The disposal failure must be logged.");
        }
        finally
        {
            await StopServiceAsync(runCancellation, runTask);
        }
    }

    [TestMethod]
    [DataRow(nameof(NimBusReceiverOptions.MaxConcurrentSessions))]
    [DataRow(nameof(NimBusReceiverOptions.MaxAutoLockRenewalDuration))]
    [DataRow(nameof(NimBusReceiverOptions.SessionIdleTimeout))]
    [DataRow(nameof(NimBusReceiverOptions.PrefetchCount))]
    [DataRow(nameof(NimBusReceiverOptions.RecoverableErrorRestartThreshold))]
    [DataRow(nameof(NimBusReceiverOptions.RecoverableErrorWindow))]
    [DataRow(nameof(NimBusReceiverOptions.MaxRecoverableErrorDelay))]
    [DataRow(nameof(NimBusReceiverOptions.RecoverableErrorDelay))]
    [DataRow(nameof(NimBusReceiverOptions.ProcessorRestartDelay))]
    [DataRow(nameof(NimBusReceiverOptions.ProcessorShutdownTimeout))]
    public void InvalidOption_IsRejectedByName(string option)
    {
        var options = new NimBusReceiverOptions { TopicName = "orders", SubscriptionName = "orders" };
        switch (option)
        {
            case nameof(NimBusReceiverOptions.MaxConcurrentSessions): options.MaxConcurrentSessions = 0; break;
            case nameof(NimBusReceiverOptions.MaxAutoLockRenewalDuration): options.MaxAutoLockRenewalDuration = TimeSpan.FromSeconds(-1); break;
            case nameof(NimBusReceiverOptions.SessionIdleTimeout): options.SessionIdleTimeout = TimeSpan.Zero; break;
            case nameof(NimBusReceiverOptions.PrefetchCount): options.PrefetchCount = -1; break;
            case nameof(NimBusReceiverOptions.RecoverableErrorRestartThreshold): options.RecoverableErrorRestartThreshold = 0; break;
            case nameof(NimBusReceiverOptions.RecoverableErrorWindow): options.RecoverableErrorWindow = TimeSpan.Zero; break;
            case nameof(NimBusReceiverOptions.MaxRecoverableErrorDelay): options.MaxRecoverableErrorDelay = TimeSpan.FromSeconds(-1); break;
            case nameof(NimBusReceiverOptions.RecoverableErrorDelay): options.RecoverableErrorDelay = TimeSpan.FromSeconds(-1); break;
            case nameof(NimBusReceiverOptions.ProcessorRestartDelay): options.ProcessorRestartDelay = TimeSpan.FromSeconds(-1); break;
            case nameof(NimBusReceiverOptions.ProcessorShutdownTimeout): options.ProcessorShutdownTimeout = TimeSpan.Zero; break;
        }

        var exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CreateService(new RecordingServiceBusClient(), options));

        Assert.AreEqual(option, exception.ParamName);
    }

    [TestMethod]
    public void Constructor_RejectsMissingDependencies()
    {
        var client = new RecordingServiceBusClient();
        var adapter = new NoopServiceBusAdapter();
        var options = new NimBusReceiverOptions { TopicName = "orders", SubscriptionName = "orders" };
        var logger = NullLogger<NimBusReceiverHostedService>.Instance;

        Assert.AreEqual("client", Assert.ThrowsExactly<ArgumentNullException>(() => new NimBusReceiverHostedService(null!, adapter, options, logger)).ParamName);
        Assert.AreEqual("adapter", Assert.ThrowsExactly<ArgumentNullException>(() => new NimBusReceiverHostedService(client, null!, options, logger)).ParamName);
        Assert.AreEqual("options", Assert.ThrowsExactly<ArgumentNullException>(() => new NimBusReceiverHostedService(client, adapter, null!, logger)).ParamName);
        Assert.AreEqual("logger", Assert.ThrowsExactly<ArgumentNullException>(() => new NimBusReceiverHostedService(client, adapter, options, null!)).ParamName);
    }

    private static TestableNimBusReceiverHostedService CreateService(RecordingServiceBusClient client, NimBusReceiverOptions options) =>
        new(client, new NoopServiceBusAdapter(), options, NullLogger<NimBusReceiverHostedService>.Instance);

    private static NimBusReceiverOptions RestartingOptions(int threshold) => new()
    {
        TopicName = "orders",
        SubscriptionName = "orders",
        RecoverableErrorRestartThreshold = threshold,
        RecoverableErrorDelay = TimeSpan.Zero,
        ProcessorRestartDelay = TimeSpan.Zero,
    };

    private static async Task<int> ProcessorCountAfterOneError(Exception exception)
    {
        var client = new RecordingServiceBusClient();
        var service = CreateService(client, RestartingOptions(threshold: 1));

        using var cts = new CancellationTokenSource();
        var runTask = service.RunProcessorLoopAsync(cts.Token);
        try
        {
            await WaitUntilAsync(() => client.Processors.Count == 1, () => $"ProcessorCount={client.Processors.Count}");
            await service.HandleProcessorErrorAsync(CreateErrorArgs(exception, ServiceBusErrorSource.Receive));

            // A recoverable error restarts within milliseconds; give it time to happen.
            var deadline = DateTime.UtcNow.AddMilliseconds(300);
            while (client.Processors.Count < 2 && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            return client.Processors.Count;
        }
        finally
        {
            await StopServiceAsync(cts, runTask);
        }
    }

    private sealed class RecordingServiceBusAdapter : IServiceBusAdapter
    {
        private int _handledMessages;

        public int HandledMessages => Volatile.Read(ref _handledMessages);

        public Task Handle(ServiceBusReceivedMessage message, ServiceBusSessionMessageActions sessionActions, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task Handle(ServiceBusReceivedMessage message, ServiceBusMessageActions messageActions, ServiceBusSessionMessageActions sessionActions, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task Handle(ServiceBusReceivedMessage message, ServiceBusSessionReceiver sessionReceiver, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task Handle(ProcessSessionMessageEventArgs args, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _handledMessages);
            return Task.CompletedTask;
        }
    }
}
