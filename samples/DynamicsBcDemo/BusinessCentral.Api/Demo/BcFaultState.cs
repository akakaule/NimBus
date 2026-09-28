namespace BusinessCentral.Api.Demo;

/// <summary>
/// The two BC failure modes the demo can switch on, each for a limited time so a scene ends by
/// itself: a scheduled update window (503 Service Unavailable) and API throttling (429 Too Many
/// Requests). They apply only to the integration APIs (/api/v2.0, /api/contoso); the BC user
/// interface keeps working and just shows a banner.
/// </summary>
public sealed class BcFaultState(TimeProvider clock)
{
    private readonly object _gate = new();
    private DateTimeOffset? _maintenanceUntil;
    private DateTimeOffset? _throttlingUntil;

    public BcFaultSnapshot Snapshot()
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            return new BcFaultSnapshot(
                Window(_maintenanceUntil, now),
                Window(_throttlingUntil, now));
        }
    }

    /// <summary>Starts an update window for <paramref name="duration"/>; zero or less ends it now.</summary>
    public BcFaultSnapshot SetMaintenance(TimeSpan duration)
    {
        lock (_gate)
        {
            _maintenanceUntil = duration > TimeSpan.Zero ? clock.GetUtcNow() + duration : null;
        }

        return Snapshot();
    }

    /// <summary>Starts throttling for <paramref name="duration"/>; zero or less ends it now.</summary>
    public BcFaultSnapshot SetThrottling(TimeSpan duration)
    {
        lock (_gate)
        {
            _throttlingUntil = duration > TimeSpan.Zero ? clock.GetUtcNow() + duration : null;
        }

        return Snapshot();
    }

    private static FaultWindow Window(DateTimeOffset? until, DateTimeOffset now) =>
        until is { } end && end > now
            ? new FaultWindow(true, end, (int)Math.Ceiling((end - now).TotalSeconds))
            : new FaultWindow(false, null, 0);
}

public sealed record BcFaultSnapshot(FaultWindow Maintenance, FaultWindow Throttling);

public sealed record FaultWindow(bool Active, DateTimeOffset? EndsAt, int RemainingSeconds);
