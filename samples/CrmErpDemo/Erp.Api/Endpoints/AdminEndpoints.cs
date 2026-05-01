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
    }
}

public record ServiceModeRequest(bool Enabled);
public record ServiceModeResponse(bool Enabled, DateTimeOffset ChangedAt);
public record ErrorModeRequest(bool Enabled);
public record ErrorModeResponse(bool Enabled, DateTimeOffset ChangedAt);
