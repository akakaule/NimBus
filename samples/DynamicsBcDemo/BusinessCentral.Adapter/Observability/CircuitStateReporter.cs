using System.Net.Http.Json;
using NimBus.Core.Extensions;

namespace BusinessCentral.Adapter.Observability;

/// <summary>
/// Pushes every circuit transition to bc-api (the adapter is a worker with no HTTP listener), so
/// the demo page shows Closed / Open / HalfOpen live. Best-effort: a diagnostic sidecar must never
/// change message handling, so failures are swallowed after a debug log.
/// </summary>
public sealed class CircuitStateReporter(CircuitStateReporterClient client, ILogger<CircuitStateReporter> logger)
    : IMessageLifecycleObserver
{
    public async Task OnCircuitStateChanged(CircuitStateChangeContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await client.ReportAsync(context, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not report circuit transition {From}->{To} for {Endpoint}.", context.From, context.To, context.Endpoint);
        }
    }
}

/// <summary>Typed HTTP client posting circuit transitions to bc-api.</summary>
public sealed class CircuitStateReporterClient(HttpClient http)
{
    public Task ReportAsync(CircuitStateChangeContext context, CancellationToken cancellationToken) =>
        http.PostAsJsonAsync(
            "/api/demo/circuit-state",
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
