namespace Erp.Api;

public sealed class ServiceModeState
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
