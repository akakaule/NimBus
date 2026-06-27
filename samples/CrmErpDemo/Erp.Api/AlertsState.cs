namespace Erp.Api;

/// <summary>
/// A received notification alert (mapped from the NimBus webhook body) plus the time the ERP API
/// received it. Surfaced in the Erp.Web "Alerts" panel.
/// </summary>
public sealed record Alert(
    string Severity,
    string Title,
    string Message,
    string EventId,
    string EventTypeId,
    string MessageId,
    string CorrelationId,
    string ErrorDetails,
    DateTimeOffset ReceivedAt);

/// <summary>
/// In-memory, thread-safe store of the most recent notification alerts received from NimBus
/// (newest first, capped). Mirrors the lock pattern of <see cref="ServiceModeState"/>; like the
/// other demo state it is intentionally non-persistent.
/// </summary>
public sealed class AlertsState
{
    private const int Capacity = 50;
    private readonly object _gate = new();
    private readonly LinkedList<Alert> _alerts = new();

    public void Add(Alert alert)
    {
        lock (_gate)
        {
            _alerts.AddFirst(alert);
            while (_alerts.Count > Capacity)
            {
                _alerts.RemoveLast();
            }
        }
    }

    public IReadOnlyList<Alert> Snapshot()
    {
        lock (_gate)
        {
            return _alerts.ToList();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _alerts.Clear();
        }
    }
}
