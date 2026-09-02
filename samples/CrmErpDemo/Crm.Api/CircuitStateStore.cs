namespace Crm.Api;

/// <summary>
/// Last known circuit-breaker state for CrmEndpoint, pushed by the CRM
/// adapter's CircuitStateReporter (the adapter is a worker with no HTTP
/// listener, so state flows adapter → api → web). Defaults to Closed: before
/// the first transition arrives, the breaker has by definition never opened.
/// </summary>
public sealed class CircuitStateStore
{
    private readonly object _gate = new();
    private CircuitStateSnapshot _current = new(
        Endpoint: "CrmEndpoint",
        State: "Closed",
        Reason: "No transitions reported yet.",
        ChangedAt: DateTimeOffset.UtcNow);

    public CircuitStateSnapshot Snapshot()
    {
        lock (_gate)
        {
            return _current;
        }
    }

    public CircuitStateSnapshot Set(string endpoint, string state, string reason, DateTimeOffset changedAt)
    {
        lock (_gate)
        {
            // Ignore out-of-order webhook deliveries — the newest transition wins.
            if (changedAt >= _current.ChangedAt)
            {
                _current = new CircuitStateSnapshot(endpoint, state, reason, changedAt);
            }

            return _current;
        }
    }
}

public sealed record CircuitStateSnapshot(string Endpoint, string State, string Reason, DateTimeOffset ChangedAt);
