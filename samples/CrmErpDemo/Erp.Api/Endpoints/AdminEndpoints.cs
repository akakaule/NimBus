namespace Erp.Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin");

        group.MapGet("/service-mode", (ServiceModeState state) =>
        {
            var (enabled, changedAt) = state.Snapshot();
            return Results.Ok(new ServiceModeResponse(enabled, changedAt));
        });

        group.MapPut("/service-mode", (ServiceModeRequest request, ServiceModeState state) =>
        {
            var (enabled, changedAt) = state.Set(request.Enabled);
            return Results.Ok(new ServiceModeResponse(enabled, changedAt));
        });

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

        group.MapGet("/processing-delay", (ProcessingDelayState state) =>
        {
            var (enabled, delayMs, changedAt) = state.Snapshot();
            return Results.Ok(new ProcessingDelayResponse(enabled, delayMs, changedAt));
        });

        group.MapPut("/processing-delay", (ProcessingDelayRequest request, ProcessingDelayState state) =>
        {
            if (request.DelayMs < ProcessingDelayState.MinDelayMs || request.DelayMs > ProcessingDelayState.MaxDelayMs)
            {
                return Results.BadRequest(new
                {
                    error = $"delayMs must be in [{ProcessingDelayState.MinDelayMs}, {ProcessingDelayState.MaxDelayMs}].",
                });
            }

            var (enabled, delayMs, changedAt) = state.Set(request.Enabled, request.DelayMs);
            return Results.Ok(new ProcessingDelayResponse(enabled, delayMs, changedAt));
        });
    }
}

public record ServiceModeRequest(bool Enabled);
public record ServiceModeResponse(bool Enabled, DateTimeOffset ChangedAt);
public record ErrorModeRequest(bool Enabled);
public record ErrorModeResponse(bool Enabled, DateTimeOffset ChangedAt);
public record ProcessingDelayRequest(bool Enabled, int DelayMs);
public record ProcessingDelayResponse(bool Enabled, int DelayMs, DateTimeOffset ChangedAt);
