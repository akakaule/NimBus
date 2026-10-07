using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using NimBus.WebApp.Mcp.Access;
using NimBus.WebApp.RateLimiting;

namespace NimBus.WebApp.Mcp.Operations;

/// <summary>
/// Caps the operator MCP tools that change a message at
/// <see cref="RateLimitOptions.McpMutations"/> per tenant, client application and user. The
/// <c>/mcp</c> route's own limit counts every tool call alike, so the tighter limit for changes
/// is counted here. A site Owner can lower it live (Spec 037); the limit is part of the
/// partition, so a changed limit starts a new window. Off when rate limiting is disabled.
/// </summary>
public sealed class OperatorMutationLimiter : IDisposable
{
    private readonly RateLimitOptions _options;
    private readonly IMcpAccessPolicyProvider _policies;
    private readonly PartitionedRateLimiter<(string Key, int Permits)> _limiter;

    /// <summary>Creates the singleton limiter.</summary>
    public OperatorMutationLimiter(IOptions<RateLimitOptions> options, IMcpAccessPolicyProvider policies)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _policies = policies;
        var window = TimeSpan.FromSeconds(_options.McpMutations.WindowSeconds);
        _limiter = PartitionedRateLimiter.Create<(string Key, int Permits), string>(caller => RateLimitPartition.GetFixedWindowLimiter(
            caller.Key + ":" + caller.Permits.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = caller.Permits,
                Window = window,
                QueueLimit = 0,
            }));
    }

    /// <summary>Takes one permit for the caller, or throws <c>[RateLimited]</c>.</summary>
    public void Acquire(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_options.Enabled)
            return;

        var permits = _policies.Current?.MutationLimit ?? _options.McpMutations.PermitLimit;
        using var lease = _limiter.AttemptAcquire((RateLimitingServiceCollectionExtensions.McpPartitionKey(context, _options), permits));
        if (!lease.IsAcquired)
            throw OperatorToolErrors.RateLimited(permits, _options.McpMutations.WindowSeconds);
    }

    /// <inheritdoc/>
    public void Dispose() => _limiter.Dispose();
}
