using Microsoft.AspNetCore.Authorization;

namespace NimBus.WebApp.Mcp.Access;

/// <summary>
/// The people, client and workload rules of the MCP access policy (Spec 037), added to the
/// <see cref="McpOperatorPolicies.Observe"/> policy in Entra mode.
/// </summary>
public sealed class McpAccessRequirement : IAuthorizationRequirement;

/// <summary>Evaluates <see cref="McpAccessRequirement"/> and audits refusals.</summary>
public sealed class McpAccessRequirementHandler : AuthorizationHandler<McpAccessRequirement>
{
    private readonly IMcpAccessPolicyProvider _policies;
    private readonly McpAccessRefusals _refusals;
    private readonly McpOperatorRuntime _runtime;
    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>Creates the handler.</summary>
    public McpAccessRequirementHandler(
        IMcpAccessPolicyProvider policies,
        McpAccessRefusals refusals,
        McpOperatorRuntime runtime,
        IHttpContextAccessor httpContextAccessor)
    {
        _policies = policies;
        _refusals = refusals;
        _runtime = runtime;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc/>
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, McpAccessRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Unauthenticated callers are left to the other requirements and the 401 challenge.
        if (context.User.Identity?.IsAuthenticated != true)
            return;

        var policy = await _policies.GetAsync().ConfigureAwait(false);
        if (policy is null)
        {
            context.Fail();
            return;
        }

        var caller = McpCaller.From(context.User, _runtime.Mode);
        var refusal = policy.CheckCaller(caller);
        if (refusal is null)
        {
            context.Succeed(requirement);
            return;
        }

        context.Fail(new AuthorizationFailureReason(this, $"MCP access policy: {McpAccessRefusals.ReasonName(refusal.Value)}"));
        var http = _httpContextAccessor.HttpContext;
        if (http is not null)
            await _refusals.RecordAsync(http, caller, refusal.Value).ConfigureAwait(false);
    }
}
