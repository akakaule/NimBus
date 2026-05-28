using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace NimBus.Extensions.Identity;

/// <summary>
/// Cookie-scheme event handlers that return a clean 401/403 for hub-path and
/// /api/ requests instead of the default 302 redirect to the login page.
///
/// Anonymous SignalR negotiate (POST /hubs/gridevents/negotiate) and anonymous
/// SPA fetch calls cannot follow a 302 to an HTML login page — the redirect
/// surfaces as a confusing "negotiate failed: unexpected response" or an
/// opaque CORS / redirect error. Returning the literal auth status lets the
/// client surface the standard "session expired" affordance.
///
/// See <see href="https://github.com/nimbus/docs/specs/010-authorize-gridevents-hub/spec.md">spec 010</see>
/// (FR-011 / FR-012).
/// </summary>
public static class NimBusCookieAuthenticationEvents
{
    // Hub path (/hubs/gridevents) mirrors NimBus.WebApp.Constants.AppEndpoints.GridEventHub.
    // Identity cannot reference NimBus.WebApp (it sits a layer below the WebApp),
    // so the string is inlined. Keep this constant in sync if the WebApp's
    // hub-mapping path ever changes.
    public const string GridEventHubPath = "/hubs/gridevents";
    public const string ApiPathPrefix = "/api/";

    /// <summary>
    /// Returns the configured <see cref="CookieAuthenticationEvents"/> instance.
    /// Hub-path and API requests get 401 (on auth challenge) or 403 (on
    /// access-denied); every other request falls through to the default
    /// 302-to-login behaviour.
    /// </summary>
    public static CookieAuthenticationEvents Create() => new()
    {
        OnRedirectToLogin = ctx =>
        {
            if (IsApiOrHubPath(ctx.Request.Path))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
            ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        },
        OnRedirectToAccessDenied = ctx =>
        {
            if (IsApiOrHubPath(ctx.Request.Path))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }
            ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        },
    };

    /// <summary>
    /// Returns true when the request targets the SignalR hub or any
    /// /api/* path — the surfaces that need a clean 401/403 instead of a 302.
    /// </summary>
    public static bool IsApiOrHubPath(PathString path)
    {
        if (!path.HasValue) return false;
        return path.StartsWithSegments(GridEventHubPath, StringComparison.OrdinalIgnoreCase)
            || path.Value!.StartsWith(ApiPathPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
