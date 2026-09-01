using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;

namespace NimBus.Core.CircuitBreaker;

/// <summary>Configures the failure-rate circuit breaker for one subscriber endpoint.</summary>
public sealed class CircuitBreakerOptions
{
    private readonly List<Func<Exception, bool>> _exclusions = [];

    /// <summary>Gets or sets the number of outcomes required before evaluation. Default: 10.</summary>
    public int MinimumThroughput { get; set; } = 10;

    /// <summary>Gets or sets the percentage that opens the circuit. Default: 50.</summary>
    public double FailurePercentageThreshold { get; set; } = 50;

    /// <summary>Gets or sets the sliding sampling window. Default: 2 minutes.</summary>
    public TimeSpan SamplingWindow { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets or sets how long an open circuit pauses receivers. Default: 1 minute.</summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets or sets successful half-open probes required to close. Default: 3.</summary>
    public int HalfOpenProbeCount { get; set; } = 3;

    /// <summary>Gets or sets whether permanent failures contribute. Default: false.</summary>
    public bool CountPermanentFailures { get; set; }

    /// <summary>Excludes an exception type, including matching inner exceptions.</summary>
    public CircuitBreakerOptions Exclude<TException>() where TException : Exception
    {
        _exclusions.Add(exception => exception is TException);
        return this;
    }

    /// <summary>Excludes exceptions matching a predicate, including inner exceptions.</summary>
    public CircuitBreakerOptions Exclude(Func<Exception, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _exclusions.Add(predicate);
        return this;
    }

    internal void Validate()
    {
        if (MinimumThroughput <= 0)
            throw new ArgumentOutOfRangeException(nameof(MinimumThroughput), "MinimumThroughput must be greater than zero.");
        if (FailurePercentageThreshold is <= 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(FailurePercentageThreshold), "FailurePercentageThreshold must be greater than zero and at most 100.");
        if (SamplingWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(SamplingWindow), "SamplingWindow must be greater than zero.");
        if (BreakDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(BreakDuration), "BreakDuration must be greater than zero.");
        if (HalfOpenProbeCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(HalfOpenProbeCount), "HalfOpenProbeCount must be greater than zero.");
    }

    internal bool ShouldCount(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (Contains<OperationCanceledException>(exception)
            || Contains<SessionBlockedException>(exception)
            || IsExcluded(exception))
        {
            return false;
        }

        return exception is EventContextHandlerException
            || exception is TransientException
            || (CountPermanentFailures && exception is PermanentFailureException);
    }

    private bool IsExcluded(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (_exclusions.Any(exclusion => exclusion(current)))
                return true;
        }

        return false;
    }

    private static bool Contains<TException>(Exception exception) where TException : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException)
                return true;
        }

        return false;
    }
}

