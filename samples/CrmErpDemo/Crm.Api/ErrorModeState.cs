namespace Crm.Api;

/// <summary>
/// Demo toggle: when enabled, the CRM adapter's HTTP client simulates a crm-api
/// outage (synthetic 503 on every call), driving sustained handler failures on
/// CrmEndpoint so the circuit breaker showcase can open the circuit on demand.
/// Mirrors Erp.Api's ErrorModeState.
/// </summary>
public sealed class ErrorModeState
{
    private readonly object _gate = new();
    private bool _enabled;
    private DateTimeOffset _changedAt = DateTimeOffset.UtcNow;

    public (bool Enabled, DateTimeOffset ChangedAt) Snapshot()
    {
        lock (_gate)
        {
            return (_enabled, _changedAt);
        }
    }

    public (bool Enabled, DateTimeOffset ChangedAt) Set(bool enabled)
    {
        lock (_gate)
        {
            if (_enabled != enabled)
            {
                _enabled = enabled;
                _changedAt = DateTimeOffset.UtcNow;
            }

            return (_enabled, _changedAt);
        }
    }
}
