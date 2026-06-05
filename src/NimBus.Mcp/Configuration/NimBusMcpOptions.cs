namespace NimBus.Mcp.Configuration;

/// <summary>
/// Configuration options for the NimBus MCP server.
/// Bind from environment variables: NIMBUS_API_BASEURL, NIMBUS_AGENT_ID.
/// </summary>
public sealed class NimBusMcpOptions
{
    /// <summary>
    /// Base URL of the NimBus WebApp REST API.
    /// Default: http://localhost:5000
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:5000";

    /// <summary>
    /// Agent identifier sent as X-Agent-Id on every request.
    /// Default: demo-agent
    /// </summary>
    public string AgentId { get; set; } = "demo-agent";
}
