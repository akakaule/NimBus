using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using NimBus.WebApp.RateLimiting;

namespace NimBus.WebApp.Mcp.Operations;

/// <summary>
/// Caps the operator MCP tools that change a message at
/// <see cref="RateLimitOptions.McpMutations"/> per tenant, client application and user. The
/// <c>/mcp</c> route's own limit counts every tool call alike, so the tighter limit for changes
/// is counted here. Off when rate limiting is disabled.
/// </summary>
public sealed class OperatorMutationLimiter : IDisposable
{
    private readonly RateLimitOptions _options;
    private readonly PartitionedRateLimiter<string> _limiter;

    /// <summary>Creates the singleton limiter.</summary>
    public OperatorMutationLimiter(IOptions<RateLimitOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        var limits = _options.McpMutations;
        _limiter = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limits.PermitLimit,
                Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                QueueLimit = 0,
            }));
    }

    /// <summary>Takes one permit for the caller, or throws <c>[RateLimited]</c>.</summary>
    public void Acquire(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_options.Enabled)
            return;

        using var lease = _limiter.AttemptAcquire(RateLimitingServiceCollectionExtensions.McpPartitionKey(context, _options));
        if (!lease.IsAcquired)
            throw OperatorToolErrors.RateLimited(_options.McpMutations.PermitLimit, _options.McpMutations.WindowSeconds);
    }

    /// <inheritdoc/>
    public void Dispose() => _limiter.Dispose();
}
