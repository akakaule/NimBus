using System.Security.Claims;

namespace NimBus.WebApp.Mcp;

/// <summary>Authentication scheme names used only by the operator MCP endpoint.</summary>
public static class McpAuthenticationSchemes
{
    /// <summary>JWT bearer scheme validating tokens issued for the MCP resource.</summary>
    public const string Bearer = "NimBusMcp";

    /// <summary>
    /// The MCP SDK scheme that serves protected-resource metadata and writes the 401 challenge
    /// pointing clients at it. <see cref="Bearer"/> forwards its challenges here.
    /// </summary>
    public const string Challenge = "NimBusMcpChallenge";
}

/// <summary>Authorization policy names for the operator MCP endpoint.</summary>
public static class McpOperatorPolicies
{
    /// <summary>Caller may use the read-only operator tools.</summary>
    public const string Observe = "NimBusMcpObserve";
}

/// <summary>
/// The Entra permissions that admit a caller to the operator tools. Delegated callers carry
/// the scope in <c>scp</c>; approved workloads carry the app role in <c>roles</c>. These
/// gate MCP access only; endpoint roles still decide what each tool returns.
/// </summary>
public static class McpOperatorPermissions
{
    /// <summary>Delegated scope for the read-only tools.</summary>
    public const string ObserveScope = "nimbus.observe";

    /// <summary>Application role for workloads using the read-only tools.</summary>
    public const string ObserveRole = "Nimbus.Observe";

    /// <summary>True when <paramref name="user"/> holds the observe scope or app role.</summary>
    public static bool CanObserve(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var hasScope = user.FindAll("scp")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(ObserveScope, StringComparer.Ordinal);

        return hasScope || user.FindAll("roles").Any(claim => string.Equals(claim.Value, ObserveRole, StringComparison.Ordinal));
    }
}
