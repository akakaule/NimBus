#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.CircuitBreaker;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.Testing;

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
}
