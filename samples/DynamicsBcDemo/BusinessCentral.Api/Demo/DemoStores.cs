namespace BusinessCentral.Api.Demo;

/// <summary>A NimBus notification received from the Business Central adapter's webhook channel.</summary>
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

/// <summary>Body the adapter's NimBus WebhookChannel posts (shaped by its JSON template).</summary>
public sealed record NotificationWebhook(
    string? Severity,
    string? Title,
    string? Message,
    string? EventId,
    string? EventTypeId,
    string? MessageId,
    string? CorrelationId,
    string? ErrorDetails);

/// <summary>
/// The newest notifications (capped, in memory), rendered by the "#integration-alerts" page — a
/// stand-in for the Teams channel a production setup would post to with the Teams channel.
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

/// <summary>A circuit transition reported by the Business Central adapter.</summary>
public sealed record CircuitStateReport(string Endpoint, string From, string To, string? Reason, DateTimeOffset Timestamp);

/// <summary>
/// Last known circuit-breaker state of the Business Central adapter (a worker with no HTTP
/// listener, so state flows adapter → bc-api → demo page), plus a short transition history.
/// </summary>
public sealed class CircuitStateStore
{
    private const int HistoryCapacity = 20;
    private readonly object _gate = new();
    private readonly LinkedList<CircuitStateReport> _history = new();
    private CircuitStateReport _current = new("BusinessCentralEndpoint", "Closed", "Closed", "No transitions reported yet.", DateTimeOffset.UtcNow);

    public CircuitStateView Snapshot()
    {
        lock (_gate)
        {
            return new CircuitStateView(_current.To, _current.Reason, _current.Timestamp, _history.ToList());
        }
    }

    public void Report(CircuitStateReport report)
    {
        lock (_gate)
        {
            _history.AddFirst(report);
            while (_history.Count > HistoryCapacity)
            {
                _history.RemoveLast();
            }

            // Ignore out-of-order deliveries: the newest transition wins.
            if (report.Timestamp >= _current.Timestamp)
            {
                _current = report;
            }
        }
    }
}

public sealed record CircuitStateView(string State, string? Reason, DateTimeOffset ChangedAt, IReadOnlyList<CircuitStateReport> History);
