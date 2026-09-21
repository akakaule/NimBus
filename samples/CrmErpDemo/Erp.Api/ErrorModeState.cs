using CrmErpDemo.Contracts.Demo;

namespace Erp.Api;

/// <summary>
/// Demo-only knob: when enabled, every ERP adapter handler throws before doing any work.
/// <see cref="Reason"/> selects which failure it simulates (see <see cref="ErpFailureReasons"/>)
/// so the Integration Intelligence card in the NimBus WebApp has something realistic to classify.
/// </summary>
public sealed class ErrorModeState
{
    private readonly object _gate = new();
    private bool _enabled;
    private string _reason = ErpFailureReasons.Default;
    private DateTimeOffset _changedAt = DateTimeOffset.UtcNow;

    public (bool Enabled, string Reason, DateTimeOffset ChangedAt) Snapshot()
    {
        lock (_gate)
        {
            return (_enabled, _reason, _changedAt);
        }
    }

    /// <summary>
    /// Updates the switch and, when <paramref name="reason"/> is given, the selected failure.
    /// The caller validates the reason; an unknown id is ignored here so the state can never
    /// hold a value the adapter cannot map.
    /// </summary>
    public (bool Enabled, string Reason, DateTimeOffset ChangedAt) Set(bool enabled, string? reason = null)
    {
        var resolved = ErpFailureReasons.Find(reason)?.Id;
        lock (_gate)
        {
            var reasonChanged = resolved is not null && !string.Equals(_reason, resolved, StringComparison.Ordinal);
            if (_enabled != enabled || reasonChanged)
            {
                _enabled = enabled;
                if (resolved is not null) _reason = resolved;
                _changedAt = DateTimeOffset.UtcNow;
            }

            return (_enabled, _reason, _changedAt);
        }
    }
}
