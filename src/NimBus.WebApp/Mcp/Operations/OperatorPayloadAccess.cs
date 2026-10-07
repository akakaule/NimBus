using NimBus.WebApp.Mcp.Access;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Mcp.Operations;

/// <summary>
/// Decides whether the current MCP caller may see raw event payloads. The caller must hold
/// PiiReader, exactly as for the Web UI. An Entra caller must also hold the delegated
/// <see cref="McpOperatorPermissions.PayloadReadScope"/>, so an agent only sees payloads when
/// the user consented to it; workload tokens never qualify. Local development has no scopes.
/// The site Owner's MCP access policy (Spec 037) can switch payloads off for every caller.
/// </summary>
public sealed class OperatorPayloadAccess
{
    private readonly IEndpointAuthorizationService _authorization;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly McpOperatorRuntime _runtime;
    private readonly IMcpAccessPolicyProvider _policies;

    /// <summary>Creates the check for one request.</summary>
    public OperatorPayloadAccess(
        IEndpointAuthorizationService authorization,
        IHttpContextAccessor httpContextAccessor,
        McpOperatorRuntime runtime,
        IMcpAccessPolicyProvider policies)
    {
        _authorization = authorization;
        _httpContextAccessor = httpContextAccessor;
        _runtime = runtime;
        _policies = policies;
    }

    /// <summary>Whether the MCP access policy allows payloads at all.</summary>
    public bool PolicyAllowsPayloads => _policies.Current?.AllowsPayloads == true;

    /// <summary>True when the caller may see raw payloads.</summary>
    public async Task<bool> CanReadPayloadsAsync()
    {
        if (!PolicyAllowsPayloads)
            return false;

        if (!await _authorization.CanReadPiiAsync().ConfigureAwait(false))
            return false;

        if (_runtime.Mode == McpAuthenticationMode.LocalDevelopment)
            return true;

        var user = _httpContextAccessor.HttpContext?.User;
        return user is not null && McpOperatorPermissions.HasPayloadScope(user);
    }
}
