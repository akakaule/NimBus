using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.WebApp.Services.Simulation;

/// <summary>
/// The global traffic ceiling (plan Decision 11): one token bucket shared by every publisher
/// loop, refilled at <c>ceiling</c> tokens per minute with a burst of at most one second of
/// tokens. Implemented as virtual scheduling (GCRA): each acquire reserves the next free slot
/// in arrival order, so concurrent loops share the ceiling fairly and none starves.
/// </summary>
public sealed class SimulationRateLimiter
{
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private int _ceilingPerMinute;
    private DateTimeOffset _theoreticalArrival;

    /// <summary>Creates the limiter.</summary>
    public SimulationRateLimiter(int ceilingPerMinute, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        UpdateCeiling(ceilingPerMinute);
        _theoreticalArrival = _timeProvider.GetUtcNow();
    }

    /// <summary>The current ceiling in publishes per minute.</summary>
    public int CeilingPerMinute => Volatile.Read(ref _ceilingPerMinute);

    /// <summary>Changes the ceiling; applies to the next acquire.</summary>
    public void UpdateCeiling(int ceilingPerMinute)
    {
        if (ceilingPerMinute < 1)
            throw new ArgumentOutOfRangeException(nameof(ceilingPerMinute), ceilingPerMinute, "The ceiling must be at least 1 per minute.");
        Volatile.Write(ref _ceilingPerMinute, ceilingPerMinute);
    }

    /// <summary>Waits for one publish token.</summary>
    public Task AcquireAsync(CancellationToken cancellationToken)
    {
        var wait = Reserve();
        return wait <= TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(wait, _timeProvider, cancellationToken);
    }

    /// <summary>Reserves the next slot and returns how long the caller must wait for it.</summary>
    internal TimeSpan Reserve()
    {
        lock (_gate)
        {
            var ceiling = CeilingPerMinute;
            var interval = TimeSpan.FromMinutes(1) / ceiling;
            // Burst of one second's worth of tokens, never less than one token.
            var tolerance = TimeSpan.FromSeconds(1) > interval ? TimeSpan.FromSeconds(1) - interval : TimeSpan.Zero;

            var now = _timeProvider.GetUtcNow();
            var arrival = _theoreticalArrival > now ? _theoreticalArrival : now;
            var allowedAt = arrival - tolerance;
            _theoreticalArrival = arrival + interval;
            return allowedAt > now ? allowedAt - now : TimeSpan.Zero;
        }
    }
}
