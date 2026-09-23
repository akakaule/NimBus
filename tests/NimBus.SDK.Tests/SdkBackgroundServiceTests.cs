#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Core.CircuitBreaker;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.Core.Outbox;
using NimBus.SDK.Hosting;

namespace NimBus.SDK.Tests;

/// <summary>
/// The SDK's long-running loops: the outbox dispatcher poll and the circuit-state lifecycle
/// pump. Both must survive a failing iteration and stop promptly with the host.
/// </summary>
[TestClass]
public sealed class SdkBackgroundServiceTests
{
    private static readonly TimeSpan Forever = TimeSpan.FromHours(1);

    // ── OutboxDispatcherHostedService ──────────────────────────────────────────────────

    [TestMethod]
    public async Task Outbox_FullBatch_IsFollowedByAnImmediatePoll()
    {
        // A full batch means more rows may be waiting: the next poll must not wait out the
        // polling interval (an hour here).
        var outbox = new ScriptedOutbox();
        outbox.Enqueue(Rows(2));
        var sender = new CountingSender();
        using var service = new OutboxDispatcherHostedService(new OutboxDispatcher(outbox, sender), Forever, batchSize: 2);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => outbox.Polls >= 2, () => $"Polls={outbox.Polls}");
        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(2, sender.Sent);
        CollectionAssert.AreEquivalent(new[] { "out-0", "out-1" }, outbox.Dispatched.ToArray());
    }

    [TestMethod]
    public async Task Outbox_PartialBatch_WaitsForThePollingInterval()
    {
        var outbox = new ScriptedOutbox();
        outbox.Enqueue(Rows(1));
        using var service = new OutboxDispatcherHostedService(new OutboxDispatcher(outbox, new CountingSender()), Forever, batchSize: 10);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => outbox.Polls >= 1, () => $"Polls={outbox.Polls}");
        await Task.Delay(200);
        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(1, outbox.Polls, "A partial batch drained the outbox; the next poll waits for the interval.");
    }

    [TestMethod]
    public async Task Outbox_FailedPoll_IsRetriedOnTheNextInterval()
    {
        // A transient store failure must not end the dispatcher for the life of the host.
        var outbox = new ScriptedOutbox { FailuresBeforeSuccess = 1 };
        outbox.Enqueue(Rows(1));
        var sender = new CountingSender();
        using var service = new OutboxDispatcherHostedService(
            new OutboxDispatcher(outbox, sender), TimeSpan.FromMilliseconds(20), batchSize: 10);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => sender.Sent == 1, () => $"Polls={outbox.Polls}, Sent={sender.Sent}");
        await service.StopAsync(CancellationToken.None);

        Assert.IsTrue(outbox.Polls >= 2);
        Assert.IsFalse(service.ExecuteTask!.IsFaulted);
    }

    [TestMethod]
    public async Task Outbox_Stop_EndsTheLoopPromptly()
    {
        using var service = new OutboxDispatcherHostedService(new OutboxDispatcher(new ScriptedOutbox(), new CountingSender()), Forever, batchSize: 10);

        await service.StartAsync(CancellationToken.None);
        var stopping = service.StopAsync(CancellationToken.None);

        Assert.AreSame(stopping, await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(5))));
        await stopping;
        Assert.IsTrue(service.ExecuteTask!.IsCompleted);
        Assert.IsFalse(service.ExecuteTask.IsFaulted, "Shutdown is not a dispatcher failure.");
    }

    [TestMethod]
    public void Outbox_RequiresADispatcher()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new OutboxDispatcherHostedService(null!, Forever, 10));
    }

    // ── CircuitBreakerLifecycleHostedService ───────────────────────────────────────────

    [TestMethod]
    public async Task Circuit_ObserverFailure_DoesNotStopLaterTransitions()
    {
        var breaker = new ManualBreaker();
        var observer = new ScriptedObserver { FailFirst = true };
        using var service = new CircuitBreakerLifecycleHostedService(
            breaker, new MessageLifecycleNotifier([observer]), NullLogger<CircuitBreakerLifecycleHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        breaker.Raise(CircuitState.Closed, CircuitState.Open);
        breaker.Raise(CircuitState.Open, CircuitState.HalfOpen);
        await WaitUntilAsync(() => observer.Seen.Count == 2, () => $"Seen={observer.Seen.Count}");
        await service.StopAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new[] { CircuitState.Open, CircuitState.HalfOpen }, observer.Seen.ToArray());
    }

    [TestMethod]
    public async Task Circuit_StopWhileAnObserverIsRunning_EndsPromptly()
    {
        var breaker = new ManualBreaker();
        var observer = new ScriptedObserver { BlockUntilCancelled = true };
        using var service = new CircuitBreakerLifecycleHostedService(
            breaker, new MessageLifecycleNotifier([observer]), NullLogger<CircuitBreakerLifecycleHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        breaker.Raise(CircuitState.Closed, CircuitState.Open);
        await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = service.StopAsync(CancellationToken.None);

        Assert.AreSame(stopping, await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.IsFalse(service.ExecuteTask!.IsFaulted);
    }

    [TestMethod]
    public async Task Circuit_Dispose_UnsubscribesFromTheBreaker()
    {
        var breaker = new ManualBreaker();
        var observer = new ScriptedObserver();
        var service = new CircuitBreakerLifecycleHostedService(
            breaker, new MessageLifecycleNotifier([observer]), NullLogger<CircuitBreakerLifecycleHostedService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        service.Dispose();
        breaker.Raise(CircuitState.Closed, CircuitState.Open);

        Assert.AreEqual(0, breaker.SubscriberCount);
        Assert.AreEqual(0, observer.Seen.Count);
    }

    private static List<OutboxMessage> Rows(int count) =>
        Enumerable.Range(0, count).Select(i => new OutboxMessage
        {
            Id = $"out-{i}",
            MessageId = $"msg-{i}",
            To = "Orders",
            Payload = JsonConvert.SerializeObject(new Message
            {
                MessageId = $"msg-{i}",
                To = "Orders",
                SessionId = "s-1",
                MessageType = MessageType.EventRequest,
                MessageContent = new MessageContent(),
            }),
            CreatedAtUtc = DateTime.UtcNow,
        }).ToList();

    private static async Task WaitUntilAsync(Func<bool> predicate, Func<string> describe)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            if (timeout.IsCancellationRequested)
                Assert.Fail($"Timed out waiting for condition. {describe()}");
            await Task.Delay(10);
        }
    }

    private sealed class ScriptedOutbox : IOutbox
    {
        private readonly ConcurrentQueue<List<OutboxMessage>> _batches = new();
        private int _polls;

        public int Polls => Volatile.Read(ref _polls);
        public int FailuresBeforeSuccess { get; set; }
        public ConcurrentBag<string> Dispatched { get; } = new();

        public void Enqueue(List<OutboxMessage> batch) => _batches.Enqueue(batch);

        public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default)
        {
            var poll = Interlocked.Increment(ref _polls);
            if (poll <= FailuresBeforeSuccess)
                return Task.FromException<IReadOnlyList<OutboxMessage>>(new InvalidOperationException("outbox table unavailable"));

            return Task.FromResult<IReadOnlyList<OutboxMessage>>(
                _batches.TryDequeue(out var batch) ? batch : new List<OutboxMessage>());
        }

        public Task MarkAsDispatchedAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
        {
            foreach (var id in ids)
                Dispatched.Add(id);
            return Task.CompletedTask;
        }

        public Task MarkAsDispatchedAsync(string id, CancellationToken cancellationToken = default) =>
            MarkAsDispatchedAsync(new[] { id }, cancellationToken);

        public Task StoreAsync(OutboxMessage message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CountingSender : ISender
    {
        private int _sent;

        public int Sent => Volatile.Read(ref _sent);

        public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _sent);
            return Task.CompletedTask;
        }

        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            Interlocked.Add(ref _sent, messages.Count());
            return Task.CompletedTask;
        }

        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _sent);
            return Task.FromResult(1L);
        }

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ManualBreaker : IEndpointCircuitBreaker
    {
        private Action<CircuitStateChange>? _stateChanged;

        public event Action<CircuitStateChange>? StateChanged
        {
            add => _stateChanged += value;
            remove => _stateChanged -= value;
        }

        public int SubscriberCount => _stateChanged?.GetInvocationList().Length ?? 0;
        public string Endpoint => "billing";
        public CircuitState State { get; private set; }
        public void RecordSuccess() { }
        public void RecordFailure(Exception exception) { }

        public Task<CircuitStateChange> WaitForStateChangeAsync(CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith<CircuitStateChange>(_ => throw new OperationCanceledException(cancellationToken), TaskScheduler.Default);

        public void Raise(CircuitState from, CircuitState to)
        {
            State = to;
            _stateChanged?.Invoke(new CircuitStateChange(Endpoint, from, to, "test", DateTimeOffset.UtcNow));
        }
    }

    private sealed class ScriptedObserver : IMessageLifecycleObserver
    {
        private int _calls;

        public bool FailFirst { get; init; }
        public bool BlockUntilCancelled { get; init; }
        public ConcurrentQueue<CircuitState> Seen { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task OnCircuitStateChanged(CircuitStateChangeContext context, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            if (BlockUntilCancelled)
                await Task.Delay(Timeout.Infinite, cancellationToken);

            Seen.Enqueue(context.To);
            if (FailFirst && Interlocked.Increment(ref _calls) == 1)
                throw new InvalidOperationException("observer failed");
        }
    }
}
