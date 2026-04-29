using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;

namespace NimBus.WebApp.Middleware
{
    /// <summary>
    /// Middleware to add security-related HTTP headers to all responses.
    /// </summary>
    public class SecurityHeadersMiddleware
    {
        private readonly RequestDelegate _next;

        public SecurityHeadersMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // Prevent clickjacking by disallowing embedding in frames
            context.Response.Headers["X-Frame-Options"] = "DENY";

            // Prevent MIME type sniffing
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";

            // Disable the legacy XSS auditor. Modern guidance (OWASP, Chrome, MDN) is to set 0:
            // the filter is deprecated, removed in current browsers, and known to introduce
            // XS-Leak vulnerabilities in older ones. CSP is the replacement.
            context.Response.Headers["X-XSS-Protection"] = "0";

            // Control referrer information sent with requests
            context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

            // Content Security Policy - restrict resource loading
            // Skip CSP for Swagger UI paths to avoid compatibility issues
            var path = context.Request.Path.Value ?? string.Empty;
            if (!path.StartsWith("/swagger", System.StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("/api-docs", System.StringComparison.OrdinalIgnoreCase))
            {
                // Drop 'unsafe-eval' from script-src — the production Vite bundle does not need it,
                // and keeping it materially weakens the XSS mitigation CSP is here to provide.
                // 'unsafe-inline' is retained for now (some Razor views emit inline script) but should
                // be replaced with a nonce-based policy in a follow-up.
                context.Response.Headers["Content-Security-Policy"] =
                    "default-src 'self'; " +
                    "script-src 'self' 'unsafe-inline'; " +
                    "style-src 'self' 'unsafe-inline'; " +
                    "img-src 'self' data: https:; " +
                    "font-src 'self'; " +
                    "connect-src 'self' wss: https:; " +
                    "frame-ancestors 'none';";
            }

            // Permissions Policy - restrict browser features
            context.Response.Headers["Permissions-Policy"] =
                "accelerometer=(), " +
                "camera=(), " +
                "geolocation=(), " +
                "gyroscope=(), " +
                "magnetometer=(), " +
                "microphone=(), " +
                "payment=(), " +
                "usb=()";

            await _next(context);
        }
    }
}
