using BusinessCentral.Api.Domain;

namespace BusinessCentral.Api.Demo;

/// <summary>
/// Makes the integration APIs answer the way Business Central online does during an update window
/// (503) or when the per-user rate limit is exceeded (429), both with Retry-After. The failures
/// happen at the BC API itself — not in the adapter — so the adapter takes its real error path.
/// </summary>
public sealed class BcFaultInjectionMiddleware(RequestDelegate next, BcFaultState faults)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/api/v2.0", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/contoso", StringComparison.OrdinalIgnoreCase))
        {
            var snapshot = faults.Snapshot();
            if (snapshot.Maintenance.Active)
            {
                await RejectAsync(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    snapshot.Maintenance.RemainingSeconds,
                    BcErrorBody.Of(
                        "ServiceUnavailable_UpdateWindow",
                        "Business Central is being updated (scheduled update window). The environment is temporarily unavailable; try again later."));
                return;
            }

            if (snapshot.Throttling.Active)
            {
                await RejectAsync(
                    context,
                    StatusCodes.Status429TooManyRequests,
                    Math.Min(snapshot.Throttling.RemainingSeconds, 5),
                    BcErrorBody.Of(
                        "Application_TooManyRequests",
                        "Too many requests: the per-user OData rate limit is exceeded. Retry after the Retry-After interval."));
                return;
            }
        }

        await next(context);
    }

    private static async Task RejectAsync(HttpContext context, int statusCode, int retryAfterSeconds, BcErrorBody body)
    {
        context.Response.StatusCode = statusCode;
        context.Response.Headers.RetryAfter = Math.Max(1, retryAfterSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await context.Response.WriteAsJsonAsync(body);
    }
}
