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

    /// <summary>The data source could not answer.</summary>
    public static McpException SourceUnavailable(string detail)
        => new($"[SourceUnavailable] {detail}");
}
