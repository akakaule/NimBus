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
/// How the deferred-processor host settles each trigger. A wrong settlement here either
/// loses the replay of a parked session (completed after a failure) or replays it forever
/// (abandoned on shutdown), so every branch is pinned through the processor's real callback.
/// </summary>
public partial class DeferredMessageProcessorHostedServiceTests
{
    [TestMethod]
    public async Task Trigger_ReplaysItsSessionAndCompletes()
    {
        var deferred = new ScriptedDeferredProcessor();
        await using var harness = await TriggerHarness.StartAsync(deferred);

        await harness.RaiseAsync(sessionId: "order-42");

        CollectionAssert.AreEqual(new[] { ("order-42", "orders") }, deferred.Calls);
        Assert.AreEqual(1, harness.Receiver.Completed);
        Assert.AreEqual(0, harness.Receiver.Abandoned + harness.Receiver.DeadLettered);
    }

    [TestMethod]
    public async Task TriggerWithoutSession_IsDeadLetteredWithoutReplaying()
    {
        var deferred = new ScriptedDeferredProcessor();
        await using var harness = await TriggerHarness.StartAsync(deferred);

        await harness.RaiseAsync(sessionId: null);

        Assert.AreEqual(0, deferred.Calls.Count);
        Assert.AreEqual(1, harness.Receiver.DeadLettered);
        Assert.AreEqual("No SessionId", harness.Receiver.LastDeadLetterReason);
        Assert.AreEqual(0, harness.Receiver.Completed);
    }

    [TestMethod]
    public async Task SessionWithNothingParked_CompletesTheTrigger()
    {
        // SessionCannotBeLocked means nothing is parked for the session right now.
        var deferred = new ScriptedDeferredProcessor
        {
            Failure = new ServiceBusException("no session", ServiceBusFailureReason.SessionCannotBeLocked),
        };
        await using var harness = await TriggerHarness.StartAsync(deferred);

        await harness.RaiseAsync(sessionId: "order-42");

        Assert.AreEqual(1, harness.Receiver.Completed);
        Assert.AreEqual(0, harness.Receiver.Abandoned);
    }

    [TestMethod]
    public async Task ReplayFailure_AbandonsTheTriggerForRedelivery()
    {
        var deferred = new ScriptedDeferredProcessor { Failure = new InvalidOperationException("replay failed") };
        await using var harness = await TriggerHarness.StartAsync(deferred);

        await harness.RaiseAsync(sessionId: "order-42");

        Assert.AreEqual(1, harness.Receiver.Abandoned, "A failed replay must be retried, not completed.");
        Assert.AreEqual(0, harness.Receiver.Completed + harness.Receiver.DeadLettered);
    }

    [TestMethod]
    public async Task ReplayFailureWhenAbandonAlsoFails_DoesNotEscapeTheCallback()
    {
        var deferred = new ScriptedDeferredProcessor { Failure = new InvalidOperationException("replay failed") };
        await using var harness = await TriggerHarness.StartAsync(deferred);
        harness.Receiver.AbandonFailure = new ServiceBusException("lock lost", ServiceBusFailureReason.MessageLockLost);

        await harness.RaiseAsync(sessionId: "order-42");

        Assert.AreEqual(1, harness.Receiver.Abandoned);
    }

    [TestMethod]
    public async Task ShutdownDuringReplay_LeavesTheTriggerUnsettled()
    {
        // Abandoning on shutdown would count the stop as a failed delivery; the lock is left to
        // expire instead so the trigger is redelivered on the next start.
        var deferred = new ScriptedDeferredProcessor { BlockUntilCancelled = true };
        var harness = await TriggerHarness.StartAsync(deferred);

        var raising = harness.RaiseAsync(sessionId: "order-42");
        await deferred.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.Service.StopAsync(CancellationToken.None);
        deferred.Release.TrySetCanceled();
        await raising.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(0, harness.Receiver.Completed + harness.Receiver.Abandoned + harness.Receiver.DeadLettered);
        harness.Service.Dispose();
    }

    [TestMethod]
    public async Task ProcessorError_IsLoggedWithoutFaultingTheHost()
    {
        await using var harness = await TriggerHarness.StartAsync(new ScriptedDeferredProcessor());

        await harness.Processor.RaiseErrorAsync(new ServiceBusException("transient", ServiceBusFailureReason.ServiceBusy));

        Assert.IsFalse(harness.Service.ExecuteTask!.IsFaulted);
    }

    private sealed class TriggerHarness : IAsyncDisposable
    {
        private TriggerHarness(DeferredMessageProcessorHostedService service, RecordingProcessor processor)
        {
            Service = service;
            Processor = processor;
        }

        public DeferredMessageProcessorHostedService Service { get; }
        public RecordingProcessor Processor { get; }
        public RecordingReceiver Receiver { get; } = new();

        public static async Task<TriggerHarness> StartAsync(IDeferredMessageProcessor deferred)
        {
            var client = new RecordingClient();
            var service = new DeferredMessageProcessorHostedService(
                client,
                deferred,
                new DeferredMessageProcessorHostedServiceOptions("orders", "deferredprocessor"),
                NullLogger<DeferredMessageProcessorHostedService>.Instance);
            await service.StartAsync(CancellationToken.None);
            await WaitForProcessorAsync(client);
            return new TriggerHarness(service, client.Processors[0]);
        }

        public Task RaiseAsync(string? sessionId) =>
            Processor.RaiseMessageAsync(
                ServiceBusModelFactory.ServiceBusReceivedMessage(messageId: "trigger-1", sessionId: sessionId),
                Receiver);

        public async ValueTask DisposeAsync()
        {
            await Service.StopAsync(CancellationToken.None);
            Service.Dispose();
        }

        private static async Task WaitForProcessorAsync(RecordingClient client)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (client.Processors.Count == 0 || client.Processors[0].StartCalls == 0)
            {
                await Task.Delay(10, timeout.Token);
            }
        }
    }

    private sealed class ScriptedDeferredProcessor : IDeferredMessageProcessor
    {
        public List<(string SessionId, string TopicName)> Calls { get; } = new();
        public Exception? Failure { get; init; }
        public bool BlockUntilCancelled { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ProcessDeferredMessagesAsync(string sessionId, string topicName, CancellationToken cancellationToken = default)
        {
            Calls.Add((sessionId, topicName));
            Started.TrySetResult();
            if (BlockUntilCancelled)
            {
                await Release.Task; // Completes as cancelled: the replay observed shutdown.
            }

            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }

    private sealed class RecordingReceiver : ServiceBusReceiver
    {
        public int Completed { get; private set; }
        public int Abandoned { get; private set; }
        public int DeadLettered { get; private set; }
        public string? LastDeadLetterReason { get; private set; }
        public Exception? AbandonFailure { get; set; }

        public override Task CompleteMessageAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken = default)
        {
            Completed++;
            return Task.CompletedTask;
        }

        public override Task AbandonMessageAsync(
            ServiceBusReceivedMessage message,
            IDictionary<string, object>? propertiesToModify = null,
            CancellationToken cancellationToken = default)
        {
            Abandoned++;
            return AbandonFailure is null ? Task.CompletedTask : Task.FromException(AbandonFailure);
        }

        public override Task DeadLetterMessageAsync(
            ServiceBusReceivedMessage message,
            string deadLetterReason,
            string? deadLetterErrorDescription = null,
            CancellationToken cancellationToken = default)
        {
            DeadLettered++;
            LastDeadLetterReason = deadLetterReason;
            return Task.CompletedTask;
        }

        public override Task DeadLetterMessageAsync(
            ServiceBusReceivedMessage message,
            IDictionary<string, object> propertiesToModify,
            string deadLetterReason,
            string? deadLetterErrorDescription = null,
            CancellationToken cancellationToken = default)
        {
            DeadLettered++;
            LastDeadLetterReason = deadLetterReason;
            return Task.CompletedTask;
        }
    }
}
