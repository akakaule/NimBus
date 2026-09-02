namespace Crm.Api.Endpoints;

/// <summary>
/// Demo admin surface for the circuit-breaker showcase: the outage toggle the
/// operator flips (mirroring Erp.Api's error mode), the live circuit state the
/// SPA polls, and the webhook the CRM adapter pushes transitions into.
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin");

        group.MapGet("/error-mode", (ErrorModeState state) =>
        {
            var (enabled, changedAt) = state.Snapshot();
            return Results.Ok(new ErrorModeResponse(enabled, changedAt));
        });

        group.MapPut("/error-mode", (ErrorModeRequest request, ErrorModeState state) =>
        {
            var (enabled, changedAt) = state.Set(request.Enabled);
            return Results.Ok(new ErrorModeResponse(enabled, changedAt));
        });

        group.MapGet("/circuit-state", (CircuitStateStore store) =>
            Results.Ok(store.Snapshot()));

        // Pushed by Crm.Adapter's CircuitStateReporter on every transition.
        app.MapPost("/api/webhooks/circuit-state", (CircuitStateWebhook payload, CircuitStateStore store) =>
        {
            store.Set(
                payload.Endpoint ?? "CrmEndpoint",
                payload.To ?? "Closed",
                payload.Reason ?? string.Empty,
                payload.Timestamp ?? DateTimeOffset.UtcNow);
            return Results.Accepted();
        });
    }
}

public record ErrorModeRequest(bool Enabled);
public record ErrorModeResponse(bool Enabled, DateTimeOffset ChangedAt);
public record CircuitStateWebhook(string? Endpoint, string? From, string? To, string? Reason, DateTimeOffset? Timestamp);
