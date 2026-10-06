using ModelContextProtocol;

namespace NimBus.WebApp.Mcp.Operations;

/// <summary>
/// Tool errors returned to the agent as <c>isError</c> results. Each message starts with a
/// stable code in brackets so agents can branch on it. Missing and forbidden resources share
/// one code, so an error never reveals whether something the caller cannot read exists.
/// </summary>
public static class OperatorToolErrors
{
    /// <summary>The endpoint does not exist or the caller cannot read it.</summary>
    public static McpException EndpointNotFound(string endpointId)
        => new($"[EndpointNotFound] No endpoint '{endpointId}' that you can read. Call nimbus_list_endpoints for the endpoints available to you.");

    /// <summary>The message does not exist on the endpoint or the caller cannot read it.</summary>
    public static McpException MessageNotFound(string endpointId, string id)
        => new($"[MessageNotFound] No message '{id}' on endpoint '{endpointId}' that you can read.");

    /// <summary>An argument is missing or invalid.</summary>
    public static McpException InvalidArgument(string detail)
        => new($"[InvalidArgument] {detail}");

    /// <summary>The cursor was not issued for this query and caller.</summary>
    public static McpException InvalidCursor()
        => new("[InvalidCursor] The cursor does not belong to this query. Repeat the query without a cursor.");

    /// <summary>The caller lacks a site-wide role or capability the tool requires.</summary>
    public static McpException PermissionDenied(string detail)
        => new($"[PermissionDenied] {detail}");

    /// <summary>An optional feature is not enabled in this deployment.</summary>
    public static McpException FeatureUnavailable(string detail)
        => new($"[FeatureUnavailable] {detail}");

    /// <summary>
    /// The message changed since it was read, or the action token is expired or not valid for
    /// this call. Nothing was sent.
    /// </summary>
    public static McpException StaleMessage(string detail)
        => new($"[StaleMessage] {detail} Nothing was sent. Read the message again and call nimbus_prepare_action for a new token.");

    /// <summary>The message's current state does not allow the action.</summary>
    public static McpException ActionNotAllowed(string detail)
        => new($"[ActionNotAllowed] {detail}");

    /// <summary>The caller sent too many changes in the current window.</summary>
    public static McpException RateLimited(int permits, int windowSeconds)
        => new($"[RateLimited] At most {permits} changes per {windowSeconds} seconds. Wait and try again.");

    /// <summary>The audit log could not record the action, so it was not run.</summary>
    public static McpException AuditUnavailable()
        => new("[AuditUnavailable] The audit log could not record the action, so it was not run. Try again later.");

    /// <summary>Sending the command failed after it was accepted; the message was restored.</summary>
    public static McpException OutcomeUnknown(string detail)
        => new($"[OutcomeUnknown] {detail} Read the message again before you retry.");

    /// <summary>The data source could not answer.</summary>
    public static McpException SourceUnavailable(string detail)
        => new($"[SourceUnavailable] {detail}");
}
