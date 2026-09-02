using System.Net.Http.Json;
using NimBus.Core.Extensions;

namespace Crm.Adapter.Observability;

/// <summary>
/// Circuit-breaker showcase: pushes every circuit transition to crm-api's
/// /api/webhooks/circuit-state so the SPA can show a live Closed / Open /
/// HalfOpen indicator (the adapter is a worker with no HTTP listener, so state
/// flows adapter → api → web). Also an SDK extensibility example — the
/// lifecycle hook is default-implemented, so this observer only overrides the
/// one event it cares about. Best-effort by design: a diagnostic sidecar must
/// never alter message handling, so every failure is swallowed after a debug log.
/// </summary>
public sealed class CircuitStateReporter(
    CircuitStateReporterClient client,
    ILogger<CircuitStateReporter> logger) : IMessageLifecycleObserver
{
    public async Task OnCircuitStateChanged(
        CircuitStateChangeContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await client.ReportAsync(context, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Could not report circuit transition {FromState}->{ToState} for {Endpoint} to crm-api.",
                context.From,
                context.To,
                context.Endpoint);
        }
    }
}

/// <summary>Typed HTTP client posting circuit transitions to crm-api.</summary>
public sealed class CircuitStateReporterClient(HttpClient http)
{
    public Task ReportAsync(CircuitStateChangeContext context, CancellationToken cancellationToken) =>
        http.PostAsJsonAsync(
            "/api/webhooks/circuit-state",
            new
            {
                endpoint = context.Endpoint,
                from = context.From.ToString(),
                to = context.To.ToString(),
                reason = context.Reason,
                timestamp = context.Timestamp,
            },
            cancellationToken);
}
