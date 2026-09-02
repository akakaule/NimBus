#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.CircuitBreaker;
using NimBus.Core.Events;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.SDK.EventHandlers;
using NimBus.Testing;
using NimBus.Testing.Extensions;

namespace NimBus.Core.Tests;

[TestClass]
public sealed class CircuitBreakerTests
{
    private static readonly DateTimeOffset Start = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Below_minimum_throughput_stays_closed()
    {
        var breaker = CreateBreaker(minimumThroughput: 3, failurePercentage: 50);

        breaker.RecordFailure(new EventContextHandlerException(new InvalidOperationException("down")));
        breaker.RecordFailure(new EventContextHandlerException(new InvalidOperationException("down")));

        Assert.AreEqual(CircuitState.Closed, breaker.State);
    }

    [TestMethod]
    public void Threshold_at_minimum_throughput_opens()
    {
        var breaker = CreateBreaker(minimumThroughput: 4, failurePercentage: 50);

        breaker.RecordFailure(new EventContextHandlerException(new InvalidOperationException("down")));
        breaker.RecordSuccess();
        breaker.RecordFailure(new TransientException("transport"));
        breaker.RecordSuccess();

        Assert.AreEqual(CircuitState.Open, breaker.State);
    }

    [TestMethod]
    public void Expired_outcomes_do_not_contribute_to_threshold()
    {
        var clock = new MutableTimeProvider(Start);
        var breaker = CreateBreaker(clock, minimumThroughput: 2, failurePercentage: 50, samplingWindow: TimeSpan.FromMinutes(1));

        breaker.RecordFailure(new EventContextHandlerException(new InvalidOperationException("old")));
        clock.Advance(TimeSpan.FromMinutes(2));
        breaker.RecordSuccess();

        Assert.AreEqual(CircuitState.Closed, breaker.State);
    }

    [TestMethod]
    public void Break_duration_moves_open_circuit_to_half_open()
    {
        var clock = new MutableTimeProvider(Start);
        var breaker = CreateBreaker(clock, breakDuration: TimeSpan.FromMinutes(1));
        breaker.RecordFailure(new EventContextHandlerException(new InvalidOperationException("down")));

        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);
    }

    [TestMethod]
    public void Half_open_probe_successes_close_circuit()
    {
        var clock = new MutableTimeProvider(Start);
        var breaker = CreateBreaker(clock, breakDuration: TimeSpan.FromMinutes(1), halfOpenProbeCount: 2);
        breaker.RecordFailure(new EventContextHandlerException(new InvalidOperationException("down")));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);

        breaker.RecordSuccess();
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);
        breaker.RecordSuccess();

        Assert.AreEqual(CircuitState.Closed, breaker.State);
    }

    [TestMethod]
    public void Half_open_probe_failure_reopens_circuit_for_a_fresh_break()
    {
        var clock = new MutableTimeProvider(Start);
        var breaker = CreateBreaker(clock, breakDuration: TimeSpan.FromMinutes(1));
        breaker.RecordFailure(new EventContextHandlerException(new InvalidOperationException("down")));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);

        breaker.RecordFailure(new TransientException("still down"));
        clock.Advance(TimeSpan.FromSeconds(59));

        Assert.AreEqual(CircuitState.Open, breaker.State);
    }

    [TestMethod]
    public void Permanent_failures_only_count_when_enabled()
    {
        var ignored = CreateBreaker();
        ignored.RecordFailure(new PermanentFailureException(new FormatException("poison")));
        Assert.AreEqual(CircuitState.Closed, ignored.State);

        var counted = CreateBreaker(countPermanentFailures: true);
        counted.RecordFailure(new PermanentFailureException(new FormatException("poison")));
        Assert.AreEqual(CircuitState.Open, counted.State);
    }

    [TestMethod]
    public void Session_block_cancellation_unknown_and_excluded_failures_do_not_count()
    {
        var options = CreateOptions();
        options.Exclude<ExpectedDependencyException>();
        var breaker = new EndpointCircuitBreaker("billing", options, new MutableTimeProvider(Start));

        breaker.RecordFailure(new SessionBlockedException());
        breaker.RecordFailure(new OperationCanceledException());
        breaker.RecordFailure(new InvalidOperationException("unknown"));
        breaker.RecordFailure(new EventContextHandlerException(new ExpectedDependencyException()));

        Assert.AreEqual(CircuitState.Closed, breaker.State);
    }

    [TestMethod]
    public async Task State_change_wait_is_broadcast_once_per_transition()
    {
        var breaker = CreateBreaker();
        var firstWaiter = breaker.WaitForStateChangeAsync(CancellationToken.None);
        var secondWaiter = breaker.WaitForStateChangeAsync(CancellationToken.None);

        breaker.RecordFailure(new EventContextHandlerException(new InvalidOperationException("down")));

        var first = await firstWaiter;
        var second = await secondWaiter;
        Assert.AreEqual(CircuitState.Open, first.To);
        Assert.AreEqual(first, second);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => breaker.WaitForStateChangeAsync(cts.Token));
    }

    [TestMethod]
    public async Task Recorder_rethrows_the_same_failure_and_records_it()
    {
        var breaker = CreateBreaker();
        var behavior = new CircuitBreakerRecorderBehavior(breaker);
        var expected = new EventContextHandlerException(new InvalidOperationException("down"));

        var actual = await Assert.ThrowsExactlyAsync<EventContextHandlerException>(() =>
            behavior.Handle(CreateContext("orders.created"), (_, _) => throw expected));

        Assert.AreSame(expected, actual);
        Assert.AreEqual(CircuitState.Open, breaker.State);
    }

    [TestMethod]
    public async Task Recorder_does_not_record_heartbeat_outcomes()
    {
        var breaker = CreateBreaker(minimumThroughput: 2, failurePercentage: 50);
        var behavior = new CircuitBreakerRecorderBehavior(breaker);
        breaker.RecordFailure(new EventContextHandlerException(new InvalidOperationException("down")));

        await behavior.Handle(CreateContext(Heartbeat.EventTypeId), (_, _) => Task.CompletedTask);

        Assert.AreEqual(CircuitState.Closed, breaker.State);
    }

    [TestMethod]
    public void Invalid_options_fail_fast()
    {
        var options = CreateOptions();
        options.FailurePercentageThreshold = 101;

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new EndpointCircuitBreaker("billing", options, TimeProvider.System));
    }

    [TestMethod]
    public void Transition_observer_failure_never_escapes_into_message_processing()
    {
        var breaker = CreateBreaker();
        breaker.StateChanged += _ => throw new InvalidOperationException("observer failed");

        breaker.RecordFailure(new TransientException("down"));

        Assert.AreEqual(CircuitState.Open, breaker.State);
    }

    private static EndpointCircuitBreaker CreateBreaker(
        int minimumThroughput = 1,
        double failurePercentage = 100,
        TimeSpan? samplingWindow = null,
        TimeSpan? breakDuration = null,
        int halfOpenProbeCount = 1,
        bool countPermanentFailures = false) =>
        CreateBreaker(
            new MutableTimeProvider(Start),
            minimumThroughput,
            failurePercentage,
            samplingWindow,
            breakDuration,
            halfOpenProbeCount,
            countPermanentFailures);

    private static EndpointCircuitBreaker CreateBreaker(
        TimeProvider clock,
        int minimumThroughput = 1,
        double failurePercentage = 100,
        TimeSpan? samplingWindow = null,
        TimeSpan? breakDuration = null,
        int halfOpenProbeCount = 1,
        bool countPermanentFailures = false)
    {
        var options = CreateOptions();
        options.MinimumThroughput = minimumThroughput;
        options.FailurePercentageThreshold = failurePercentage;
        options.SamplingWindow = samplingWindow ?? TimeSpan.FromMinutes(2);
        options.BreakDuration = breakDuration ?? TimeSpan.FromMinutes(1);
        options.HalfOpenProbeCount = halfOpenProbeCount;
        options.CountPermanentFailures = countPermanentFailures;
        return new EndpointCircuitBreaker("billing", options, clock);
    }

    private static CircuitBreakerOptions CreateOptions() => new()
    {
        MinimumThroughput = 1,
        FailurePercentageThreshold = 100,
        SamplingWindow = TimeSpan.FromMinutes(2),
        BreakDuration = TimeSpan.FromMinutes(1),
        HalfOpenProbeCount = 1,
    };

    private static InMemoryMessageContext CreateContext(string eventTypeId) => new(
        new Message
        {
            EventId = "event-1",
            MessageId = "message-1",
            SessionId = "session-1",
            To = "billing",
            EventTypeId = eventTypeId,
            MessageType = MessageType.EventRequest,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent { EventTypeId = eventTypeId, EventJson = "{}" },
            },
        },
        new InMemorySessionState());

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan value) => _utcNow += value;
    }

    private sealed class ExpectedDependencyException : Exception;

    // ----- regression tests for the PR #116 review findings -------------------

    [TestMethod]
    public void Wrapped_downstream_timeout_counts_toward_opening()
    {
        // An HttpClient timeout surfaces as TaskCanceledException wrapped in the
        // retry-classified handler exception — the most common outage signature.
        var breaker = CreateBreaker(minimumThroughput: 1, failurePercentage: 100);

        breaker.RecordFailure(new EventContextHandlerException(new TaskCanceledException("HTTP timeout")));

        Assert.AreEqual(CircuitState.Open, breaker.State);
    }

    [TestMethod]
    public void Top_level_cancellation_does_not_count()
    {
        var breaker = CreateBreaker(minimumThroughput: 1, failurePercentage: 100);

        breaker.RecordFailure(new OperationCanceledException());

        Assert.AreEqual(CircuitState.Closed, breaker.State);
    }

    [TestMethod]
    public async Task Waiter_observes_transition_racing_the_break_expiry()
    {
        // The internal break delay elapsing and a concurrent reader (the metrics
        // gauge) advancing Open->HalfOpen are synchronized by construction at
        // openedAt + BreakDuration. The waiter must return the transition even
        // when its delay wins that race, instead of re-binding the fresh signal
        // and stranding the endpoint paused in HalfOpen.
        var clock = new ManualTimerProvider(Start);
        var options = CreateOptions();
        options.BreakDuration = TimeSpan.FromMinutes(1);
        var breaker = new EndpointCircuitBreaker("billing", options, clock);
        breaker.RecordFailure(new EventContextHandlerException(new InvalidOperationException("down")));
        Assert.AreEqual(CircuitState.Open, breaker.State);

        var wait = breaker.WaitForStateChangeAsync(CancellationToken.None);
        await Task.Delay(50); // let the waiter arm its internal delay
        clock.Advance(TimeSpan.FromMinutes(1)); // completes the delay task
        _ = breaker.State; // gauge-style read performs the Open->HalfOpen advance

        var change = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(CircuitState.Open, change.From);
        Assert.AreEqual(CircuitState.HalfOpen, change.To);
    }

    [TestMethod]
    public async Task Recorder_registered_both_ways_records_each_outcome_once()
    {
        // A user following the AddPipelineBehavior<T> docs may register the
        // recorder manually alongside WithCircuitBreaker; the pipeline must
        // dedup it or every outcome double-counts and the effective thresholds
        // silently halve.
        var breaker = CreateBreaker(minimumThroughput: 2, failurePercentage: 100);
        var recorder = new CircuitBreakerRecorderBehavior(breaker);
        using var provider = new ServiceCollection()
            .AddSingleton(recorder)
            .BuildServiceProvider();
        var pipeline = new MessagePipeline(
            new PipelineBehaviorRegistry([typeof(CircuitBreakerRecorderBehavior)]),
            provider,
            [recorder]);

        await Assert.ThrowsExactlyAsync<TransientException>(() =>
            pipeline.Execute(CreateContext("OrderPlaced"), (_, _) => throw new TransientException("down")));

        // One failure recorded, below MinimumThroughput=2 — a double-recording
        // pipeline would have opened the circuit here.
        Assert.AreEqual(CircuitState.Closed, breaker.State);

        await Assert.ThrowsExactlyAsync<TransientException>(() =>
            pipeline.Execute(CreateContext("OrderPlaced"), (_, _) => throw new TransientException("down")));

        Assert.AreEqual(CircuitState.Open, breaker.State);
    }

    [TestMethod]
    public async Task Test_transport_honors_WithCircuitBreaker()
    {
        // The in-memory path must exercise the same middleware composition as
        // production — WithCircuitBreaker used to be validated and then
        // silently discarded here.
        var services = new ServiceCollection();
        services.AddNimBusTestTransport(builder =>
        {
            // TransientException: counted by the breaker and rethrown verbatim
            // through the pipeline. A generic exception would route through the
            // error-response path, which needs more message metadata than this
            // minimal fixture carries.
            builder.AddDynamicHandler(
                "OrderPlaced",
                () => new DelegateEventJsonHandler((_, _) => throw new TransientException("down")));
            builder.WithCircuitBreaker(options =>
            {
                options.MinimumThroughput = 2;
                options.FailurePercentageThreshold = 100;
            });
        });

        using var provider = services.BuildServiceProvider();
        var messageHandler = provider.GetRequiredService<IMessageHandler>();
        var breaker = provider.GetRequiredService<IEndpointCircuitBreaker>();

        await messageHandler.Handle(CreateContext("OrderPlaced"));
        Assert.AreEqual(CircuitState.Closed, breaker.State);

        await messageHandler.Handle(CreateContext("OrderPlaced"));
        Assert.AreEqual(CircuitState.Open, breaker.State);
    }

    /// <summary>
    /// TimeProvider whose timers fire only when the test advances the clock —
    /// the base TimeProvider.CreateTimer runs on real time, which cannot pin
    /// the break-expiry race deterministically.
    /// </summary>
    private sealed class ManualTimerProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object _lock = new();
        private readonly List<PendingTimer> _pending = [];
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock)
            {
                return _utcNow;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_lock)
            {
                var timer = new PendingTimer(callback, state, _utcNow + dueTime);
                _pending.Add(timer);
                return timer;
            }
        }

        public void Advance(TimeSpan delta)
        {
            PendingTimer[] due;
            lock (_lock)
            {
                _utcNow += delta;
                due = _pending.Where(timer => timer.Due <= _utcNow && !timer.Fired).ToArray();
            }

            foreach (var timer in due)
                timer.Fire();
        }

        private sealed class PendingTimer(TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            public DateTimeOffset Due { get; } = due;

            public bool Fired { get; private set; }

            public void Fire()
            {
                Fired = true;
                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
