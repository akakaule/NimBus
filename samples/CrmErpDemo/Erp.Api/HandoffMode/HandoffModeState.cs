namespace Erp.Api.HandoffMode;

// Toggle + parameters for the demo PendingHandoff showcase. The ERP adapter's
// CrmAccountCreated handler polls this via /api/admin/handoff-mode and, when
// Enabled, signals MarkPendingHandoff(...) instead of running the synchronous
// upsert. Mirrors ServiceModeState / ErrorModeState — singleton, lock-guarded.
public sealed class HandoffModeState
{
    private readonly object _gate = new();
    private bool _enabled;
    private int _durationSeconds = 5;
    private double _failureRate;
    private DateTimeOffset _changedAt = DateTimeOffset.UtcNow;

    public (bool Enabled, int DurationSeconds, double FailureRate, DateTimeOffset ChangedAt) Snapshot()
    {
        lock (_gate)
        {
            return (_enabled, _durationSeconds, _failureRate, _changedAt);
        }
    }

    public (bool Enabled, int DurationSeconds, double FailureRate, DateTimeOffset ChangedAt) Set(
        bool enabled,
        int durationSeconds,
        double failureRate)
    {
        lock (_gate)
        {
            var changed = _enabled != enabled
                || _durationSeconds != durationSeconds
                || Math.Abs(_failureRate - failureRate) > double.Epsilon;
            if (changed)
            {
                _enabled = enabled;
                _durationSeconds = durationSeconds;
                _failureRate = failureRate;
                _changedAt = DateTimeOffset.UtcNow;
            }
            return (_enabled, _durationSeconds, _failureRate, _changedAt);
        }
    }
}
