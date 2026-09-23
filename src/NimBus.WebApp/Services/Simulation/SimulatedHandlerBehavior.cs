using System;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;

namespace NimBus.WebApp.Services.Simulation;

/// <summary>
/// The body of a simulated handler for one endpoint (plan Decision 8). Runs inside the
/// real <see cref="StrictMessageHandler"/> pipeline, so every mode produces the Resolver
/// response a real handler failing the same way would. The failure is swapped atomically
/// by <see cref="Update"/>; a running host picks it up on its next delivery.
/// </summary>
public sealed class SimulatedHandlerBehavior
{
    private readonly ISimulationFeedSink _sink;
    private readonly TimeProvider _timeProvider;
    private readonly Random _random;
    private readonly object _randomLock = new();
    private FailureState _state;
    private int _inFlight;
    private long _lastActivityTicks;

    /// <summary>Creates the behavior for <paramref name="endpointId"/>, initially Healthy.</summary>
    public SimulatedHandlerBehavior(string endpointId, ISimulationFeedSink sink, TimeProvider? timeProvider = null, Random? random = null)
    {
        EndpointId = string.IsNullOrWhiteSpace(endpointId) ? throw new ArgumentException("Endpoint id is required.", nameof(endpointId)) : endpointId;
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _random = random ?? new Random();
        _state = new FailureState(new SimulatedFailure(), _timeProvider.GetUtcNow(), null);
    }

    /// <summary>The endpoint this behavior simulates.</summary>
    public string EndpointId { get; }

    /// <summary>Deliveries currently inside the handler.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>When a delivery last started or finished, or null before the first one.</summary>
    public DateTimeOffset? LastActivity
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastActivityTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>The configured failure, before scoping and revert are applied.</summary>
    public SimulatedFailure Failure => Volatile.Read(ref _state).Failure;

    /// <summary>
    /// Replaces the failure. <paramref name="appliedAt"/> starts the revert-after clock; pass the
    /// previous time when the failure did not change so an unrelated config PUT does not reset it.
    /// </summary>
    public void Update(SimulatedFailure failure, DateTimeOffset appliedAt)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Volatile.Write(ref _state, new FailureState(failure, appliedAt, CompileGlob(failure.SessionPattern)));
    }

    /// <summary>When the current failure was applied.</summary>
    public DateTimeOffset AppliedAt => Volatile.Read(ref _state).AppliedAt;

    /// <summary>The mode in force now, after revert-after.</summary>
    public SimulatedFailureMode EffectiveMode => Effective(Volatile.Read(ref _state)).Mode;

    /// <summary>Handles one delivery according to the current failure mode.</summary>
    public async Task HandleAsync(IMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var state = Volatile.Read(ref _state);
        var failure = Effective(state);
        var mode = InScope(state, failure, context) ? failure.Mode : SimulatedFailureMode.Healthy;
        var retryCount = context.RetryCount ?? 0;
        var started = Stopwatch.GetTimestamp();

        Interlocked.Increment(ref _inFlight);
        Touch();
        try
        {
            switch (mode)
            {
                case SimulatedFailureMode.Healthy:
                    await DelayAsync(Next(20, 80), cancellationToken).ConfigureAwait(false);
                    break;

                case SimulatedFailureMode.Random:
                    await DelayAsync(Next(20, 80), cancellationToken).ConfigureAwait(false);
                    if (NextPercent() < failure.Rate)
                        throw Record(context, SimulatedDeliveryOutcome.Threw, retryCount, started, new SimulatedTransientException(failure.EffectiveMessage));
                    break;

                case SimulatedFailureMode.Transient:
                    await DelayAsync(Next(20, 80), cancellationToken).ConfigureAwait(false);
                    if (retryCount < failure.FailAttempts)
                        throw Record(context, SimulatedDeliveryOutcome.Threw, retryCount, started, new SimulatedTransientException(failure.EffectiveMessage));
                    break;

                case SimulatedFailureMode.Slow:
                    await DelayAsync(Next(failure.LatencyMinMs, failure.LatencyMaxMs), cancellationToken).ConfigureAwait(false);
                    break;

                case SimulatedFailureMode.Poison:
                    throw Record(context, SimulatedDeliveryOutcome.Poisoned, retryCount, started, new SimulatedPermanentException(failure.EffectiveMessage));

                case SimulatedFailureMode.NoHandler:
                    throw Record(context, SimulatedDeliveryOutcome.Unsupported, retryCount, started, new EventHandlerNotFoundException(failure.EffectiveMessage));
            }

            Record(context, SimulatedDeliveryOutcome.Completed, retryCount, started, error: null);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
            Touch();
        }
    }

    /// <summary>True when <paramref name="sessionId"/> matches the <c>*</c>/<c>?</c> glob <paramref name="pattern"/>.</summary>
    public static bool GlobMatches(string? pattern, string? sessionId)
    {
        var regex = CompileGlob(pattern);
        return regex is null || regex.IsMatch(sessionId ?? string.Empty);
    }

    private SimulatedFailure Effective(FailureState state)
    {
        var failure = state.Failure;
        if (failure.RevertAfterMinutes is int minutes && _timeProvider.GetUtcNow() >= state.AppliedAt.AddMinutes(minutes))
            return new SimulatedFailure();
        return failure;
    }

    private static bool InScope(FailureState state, SimulatedFailure failure, IMessageContext context)
    {
        if (failure.EventTypeIds.Count > 0 && !failure.EventTypeIds.Contains(context.EventTypeId, StringComparer.Ordinal))
            return false;
        return state.SessionGlob is null || state.SessionGlob.IsMatch(context.SessionId ?? string.Empty);
    }

    private TException Record<TException>(IMessageContext context, SimulatedDeliveryOutcome outcome, int retryCount, long started, TException error)
        where TException : Exception
    {
        Record(context, outcome, retryCount, started, error.Message);
        return error;
    }

    private void Record(IMessageContext context, SimulatedDeliveryOutcome outcome, int retryCount, long started, string? error)
    {
        _sink.Record(new SimulatedDelivery(
            _timeProvider.GetUtcNow(),
            EndpointId,
            context.EventTypeId ?? string.Empty,
            context.SessionId ?? string.Empty,
            context.MessageId ?? string.Empty,
            outcome,
            retryCount + 1,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            error));
    }

    private Task DelayAsync(int milliseconds, CancellationToken cancellationToken) =>
        milliseconds <= 0
            ? Task.CompletedTask
            : Task.Delay(TimeSpan.FromMilliseconds(milliseconds), _timeProvider, cancellationToken);

    private int Next(int min, int max)
    {
        lock (_randomLock)
        {
            return min >= max ? min : _random.Next(min, max + 1);
        }
    }

    private double NextPercent()
    {
        lock (_randomLock)
        {
            return _random.NextDouble() * 100;
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivityTicks, _timeProvider.GetUtcNow().UtcTicks);

    private static Regex? CompileGlob(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return null;

        var body = Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal);
        return new Regex("^" + body + "$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    private sealed record FailureState(SimulatedFailure Failure, DateTimeOffset AppliedAt, Regex? SessionGlob);
}
