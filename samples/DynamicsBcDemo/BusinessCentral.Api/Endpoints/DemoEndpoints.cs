using BusinessCentral.Api.Demo;
using BusinessCentral.Api.Domain;
using BusinessCentral.Api.Integration;

namespace BusinessCentral.Api.Endpoints;

/// <summary>
/// Demo-only controls and sinks, used by the hidden /demo page of the BC web client: the go-live
/// initial sync, time-boxed failure modes, the notification sink behind the "#integration-alerts" page,
/// and the adapter's circuit state. Unauthenticated by design — a local demo, not a production pattern.
/// </summary>
public static class DemoEndpoints
{
    private static readonly TimeSpan MaxFaultDuration = TimeSpan.FromMinutes(10);

    public static void MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        var demo = app.MapGroup("/api/demo");

        // The go-live initial load: item categories, customers and their contacts, published through
        // the outbox in one transaction. In production this is a one-off job; CRM upserts, so it is
        // safe to run again.
        demo.MapPost("/initial-sync", async (BcUnitOfWork uow, SalesService sales, CancellationToken ct) =>
            Results.Ok(await uow.RunAsync(events => sales.BuildInitialSyncAsync(events, ct), ct)));

        demo.MapGet("/state", (BcFaultState faults, CircuitStateStore circuit, AlertsState alerts) =>
            Results.Ok(new
            {
                faults = faults.Snapshot(),
                circuit = circuit.Snapshot(),
                alertCount = alerts.Snapshot().Count,
            }));

        demo.MapPut("/maintenance", (FaultBody body, BcFaultState faults) =>
            Results.Ok(faults.SetMaintenance(Clamp(body.Seconds))));

        demo.MapPut("/throttling", (FaultBody body, BcFaultState faults) =>
            Results.Ok(faults.SetThrottling(Clamp(body.Seconds))));

        // NimBus WebhookChannel target (the adapter's notifications). The adapter sends no auth
        // header, so this sink is deliberately open; don't copy it into production.
        demo.MapPost("/alerts", (NotificationWebhook payload, AlertsState alerts) =>
        {
            alerts.Add(new Alert(
                Severity: payload.Severity ?? "Information",
                Title: payload.Title ?? string.Empty,
                Message: payload.Message ?? string.Empty,
                EventId: payload.EventId ?? string.Empty,
                EventTypeId: payload.EventTypeId ?? string.Empty,
                MessageId: payload.MessageId ?? string.Empty,
                CorrelationId: payload.CorrelationId ?? string.Empty,
                ErrorDetails: payload.ErrorDetails ?? string.Empty,
                ReceivedAt: DateTimeOffset.UtcNow));
            return Results.Accepted();
        });

        demo.MapGet("/alerts", (AlertsState alerts) => Results.Ok(alerts.Snapshot()));

        demo.MapDelete("/alerts", (AlertsState alerts) =>
        {
            alerts.Clear();
            return Results.NoContent();
        });

        demo.MapPost("/circuit-state", (CircuitStateReport report, CircuitStateStore circuit) =>
        {
            circuit.Report(report);
            return Results.Accepted();
        });

        demo.MapGet("/circuit-state", (CircuitStateStore circuit) => Results.Ok(circuit.Snapshot()));
    }

    private static TimeSpan Clamp(int seconds) =>
        seconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(seconds, MaxFaultDuration.TotalSeconds));
}

/// <summary>Body of the fault toggles: how many seconds the failure mode lasts (0 ends it).</summary>
public sealed record FaultBody(int Seconds);
