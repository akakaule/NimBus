namespace Erp.Api;

/// <summary>
/// Demo-only knob: an artificial per-message processing delay applied by the ERP
/// adapter's pipeline. When enabled, every inbound message waits
/// <c>DelayMilliseconds</c> before its handler runs, so message processing visibly
/// takes time on the Flow monitor and slow-consumer behaviour can be exercised.
/// Mirror of <see cref="ServiceModeState"/>; the value is clamped to
/// [<see cref="MinDelayMs"/>, <see cref="MaxDelayMs"/>].
/// </summary>
public sealed class ProcessingDelayState
{
    public const int MinDelayMs = 100;
    public const int MaxDelayMs = 100_000;

    private readonly object _gate = new();
    private bool _enabled;
    private int _delayMilliseconds = 2_000;
    private DateTimeOffset _changedAt = DateTimeOffset.UtcNow;

    public (bool Enabled, int DelayMilliseconds, DateTimeOffset ChangedAt) Snapshot()
    {
        lock (_gate)
        {
            return (_enabled, _delayMilliseconds, _changedAt);
        }
    }

    public (bool Enabled, int DelayMilliseconds, DateTimeOffset ChangedAt) Set(bool enabled, int delayMilliseconds)
    {
        var clamped = Math.Clamp(delayMilliseconds, MinDelayMs, MaxDelayMs);
        lock (_gate)
        {
            if (_enabled != enabled || _delayMilliseconds != clamped)
            {
                _enabled = enabled;
                _delayMilliseconds = clamped;
                _changedAt = DateTimeOffset.UtcNow;
            }
            return (_enabled, _delayMilliseconds, _changedAt);
        }
    }
}
