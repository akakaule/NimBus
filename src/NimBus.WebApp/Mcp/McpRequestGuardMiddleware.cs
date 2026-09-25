using System.Net;

namespace NimBus.WebApp.Mcp;

/// <summary>
/// Transport checks for the operator MCP endpoint that run before authentication.
/// <list type="bullet">
/// <item>A browser <c>Origin</c> must be allowed; the MCP transport requires this to stop DNS
/// rebinding. Requests without the header (non-browser clients) are unaffected.</item>
/// <item>In local development, where nothing authenticates the caller, only loopback
/// connections are accepted.</item>
/// </list>
/// </summary>
public sealed class McpRequestGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly McpOperatorRuntime _runtime;
    private readonly ILogger<McpRequestGuardMiddleware> _logger;

    /// <summary>Creates the middleware.</summary>
    public McpRequestGuardMiddleware(RequestDelegate next, McpOperatorRuntime runtime, ILogger<McpRequestGuardMiddleware> logger)
    {
        _next = next;
        _runtime = runtime;
        _logger = logger;
    }

    /// <summary>Rejects disallowed MCP requests with 403 and passes everything else on.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(McpOperatorOptions.Path, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var local = _runtime.Mode == McpAuthenticationMode.LocalDevelopment;

        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !IsAllowedOrigin(origin, local))
        {
            _logger.LogWarning("Rejected an MCP request from origin {Origin}", origin);
            await RejectAsync(context, "Origin not allowed.").ConfigureAwait(false);
            return;
        }

        var remote = context.Connection.RemoteIpAddress;
        if (local && remote is not null && !IsLoopback(remote))
        {
            _logger.LogWarning("Rejected a non-loopback MCP request from {RemoteAddress} in local development mode", remote);
            await RejectAsync(context, "The local development MCP endpoint accepts loopback connections only.").ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private bool IsAllowedOrigin(string origin, bool local)
    {
        var normalized = origin.TrimEnd('/');
        if (_runtime.Options.AllowedOrigins.Any(allowed => string.Equals(allowed.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase)))
            return true;

        return local
            && Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLoopback(IPAddress address)
        => IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    private static Task RejectAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/plain";
        return context.Response.WriteAsync(message);
    }
}
