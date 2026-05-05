namespace Erp.Api.HandoffMode;

public static class HandoffEndpoints
{
    public static void MapHandoffEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin");

        admin.MapGet("/handoff-mode", (HandoffModeState state) =>
        {
            var (enabled, durationSeconds, failureRate, changedAt) = state.Snapshot();
            return Results.Ok(new HandoffModeResponse(enabled, durationSeconds, failureRate, changedAt));
        });

        admin.MapPut("/handoff-mode", (HandoffModeRequest request, HandoffModeState state) =>
        {
            if (request.DurationSeconds < 1 || request.DurationSeconds > 600)
                return Results.BadRequest(new { error = "durationSeconds must be in [1, 600]." });
            if (request.FailureRate < 0.0 || request.FailureRate > 1.0)
                return Results.BadRequest(new { error = "failureRate must be in [0.0, 1.0]." });

            var (enabled, durationSeconds, failureRate, changedAt) = state.Set(
                request.Enabled, request.DurationSeconds, request.FailureRate);
            return Results.Ok(new HandoffModeResponse(enabled, durationSeconds, failureRate, changedAt));
        });

        var jobs = app.MapGroup("/api/internal/handoff-jobs");

        jobs.MapPost("/", (HandoffJob job, HandoffJobTracker tracker) =>
        {
            tracker.Register(job);
            return Results.Created($"/api/internal/handoff-jobs/{Uri.EscapeDataString(job.EventId)}", job);
        });

        jobs.MapGet("/", (HandoffJobTracker tracker) => Results.Ok(tracker.GetAll()));
    }
}

public record HandoffModeRequest(bool Enabled, int DurationSeconds, double FailureRate);
public record HandoffModeResponse(bool Enabled, int DurationSeconds, double FailureRate, DateTimeOffset ChangedAt);
