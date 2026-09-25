using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using NimBus.Core;
using NimBus.Extensions.IntegrationIntelligence;
using NimBus.WebApp.RateLimiting;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Mcp.Tools;

/// <summary>
/// Read-only tools that tell an agent where it is and what it may look at. Every result is
/// filtered to the endpoints the caller holds Reader on, using the same authorization as the
/// REST API.
/// </summary>
[McpServerToolType]
public sealed class OperatorDiscoveryTools
{
    private readonly IPlatform _platform;
    private readonly IEndpointAuthorizationService _authorization;
    private readonly IConfiguration _configuration;
    private readonly McpOperatorRuntime _runtime;
    private readonly RateLimitOptions _rateLimits;
    private readonly bool _classificationEnabled;

    /// <summary>Creates the tools for one request.</summary>
    public OperatorDiscoveryTools(
        IPlatform platform,
        IEndpointAuthorizationService authorization,
        IConfiguration configuration,
        McpOperatorRuntime runtime,
        IOptions<RateLimitOptions> rateLimits,
        IEnumerable<IntegrationIntelligenceActivation> intelligence)
    {
        _platform = platform;
        _authorization = authorization;
        _configuration = configuration;
        _runtime = runtime;
        _rateLimits = rateLimits.Value;
        _classificationEnabled = intelligence.Any(activation => activation.Enabled);
    }

    /// <summary>Describes the deployment, the caller and what the caller may do.</summary>
    [McpServerTool(Name = "nimbus_get_capabilities", Title = "NimBus capabilities", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Call this first. Returns the NimBus environment this server belongs to, who you are, the endpoints you can read, the actions you may take, request limits and optional features. Contains no secrets or platform configuration.")]
    public async Task<CapabilitiesResult> GetCapabilitiesAsync()
    {
        var access = await _authorization.GetCurrentUserAccessAsync().ConfigureAwait(false);
        var limits = _rateLimits.Enabled
            ? new RequestLimits(_rateLimits.Mcp.PermitLimit, _rateLimits.Mcp.WindowSeconds)
            : null;

        return new CapabilitiesResult(
            Environment,
            DateTimeOffset.UtcNow,
            new CallerInfo(_authorization.GetCurrentUserName(), access.ObjectId, access.SiteRole.ToString(), access.IsPiiReader),
            _runtime.Mode.ToString(),
            await ReadableEndpointIdsAsync().ConfigureAwait(false),
            [],
            limits,
            new FeatureInfo(_classificationEnabled));
    }

    /// <summary>Lists the endpoints the caller can read, with the event types each produces and consumes.</summary>
    [McpServerTool(Name = "nimbus_list_endpoints", Title = "List NimBus endpoints", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the NimBus endpoints you can read, with their description and the event type ids each endpoint produces and consumes. Endpoint ids are the identifiers other NimBus tools take.")]
    public async Task<EndpointListResult> ListEndpointsAsync()
    {
        var readable = (await ReadableEndpointIdsAsync().ConfigureAwait(false)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var endpoints = _platform.Endpoints
            .Where(endpoint => readable.Contains(endpoint.Id))
            .OrderBy(endpoint => endpoint.Id, StringComparer.OrdinalIgnoreCase)
            .Select(endpoint => new EndpointSummary(
                endpoint.Id,
                endpoint.Name,
                endpoint.Description,
                endpoint.EventTypesProduced.Select(eventType => eventType.Id).Order(StringComparer.Ordinal).ToList(),
                endpoint.EventTypesConsumed.Select(eventType => eventType.Id).Order(StringComparer.Ordinal).ToList()))
            .ToList();

        return new EndpointListResult(Environment, DateTimeOffset.UtcNow, endpoints);
    }

    private string? Environment => _configuration.GetValue<string>("Environment");

    private async Task<IReadOnlyList<string>> ReadableEndpointIdsAsync()
    {
        var readable = new List<string>();
        foreach (var endpoint in _platform.Endpoints)
        {
            if (EndpointVisibility.IsListed(endpoint.Id, Environment)
                && await _authorization.HasRoleAsync(AccessRole.Reader, endpoint.Id).ConfigureAwait(false))
            {
                readable.Add(endpoint.Id);
            }
        }

        return readable;
    }
}

/// <summary>Result of <c>nimbus_get_capabilities</c>.</summary>
/// <param name="Environment">The configured NimBus environment name.</param>
/// <param name="AsOfUtc">When the result was produced.</param>
/// <param name="Caller">The authenticated caller.</param>
/// <param name="AuthenticationMode"><c>Entra</c> or <c>LocalDevelopment</c>.</param>
/// <param name="ReadableEndpoints">Endpoint ids the caller holds Reader on.</param>
/// <param name="PermittedActions">Recovery actions the caller may take through MCP. Empty in this release.</param>
/// <param name="Limits">The MCP request limit, or null when rate limiting is off.</param>
/// <param name="Features">Optional features available in this deployment.</param>
public sealed record CapabilitiesResult(
    string? Environment,
    DateTimeOffset AsOfUtc,
    CallerInfo Caller,
    string AuthenticationMode,
    IReadOnlyList<string> ReadableEndpoints,
    IReadOnlyList<string> PermittedActions,
    RequestLimits? Limits,
    FeatureInfo Features);

/// <summary>The authenticated caller.</summary>
/// <param name="Name">Display name, when known.</param>
/// <param name="ObjectId">Entra object id, when the caller is an Entra principal.</param>
/// <param name="SiteRole">Site-wide role: None, Reader, Contributor or Owner.</param>
/// <param name="CanReadPayloads">Whether the caller holds PiiReader.</param>
public sealed record CallerInfo(string? Name, string? ObjectId, string SiteRole, bool CanReadPayloads);

/// <summary>A fixed-window request limit.</summary>
/// <param name="RequestsPerWindow">Requests admitted per window.</param>
/// <param name="WindowSeconds">Window length in seconds.</param>
public sealed record RequestLimits(int RequestsPerWindow, int WindowSeconds);

/// <summary>Optional features.</summary>
/// <param name="FailureClassification">Whether AI failure classification is enabled.</param>
public sealed record FeatureInfo(bool FailureClassification);

/// <summary>Result of <c>nimbus_list_endpoints</c>.</summary>
/// <param name="Environment">The configured NimBus environment name.</param>
/// <param name="AsOfUtc">When the result was produced.</param>
/// <param name="Endpoints">Readable endpoints, ordered by id.</param>
public sealed record EndpointListResult(string? Environment, DateTimeOffset AsOfUtc, IReadOnlyList<EndpointSummary> Endpoints);

/// <summary>One catalog endpoint.</summary>
/// <param name="Id">Endpoint id.</param>
/// <param name="Name">Endpoint name.</param>
/// <param name="Description">Description, when the catalog provides one.</param>
/// <param name="EventTypesProduced">Event type ids the endpoint publishes.</param>
/// <param name="EventTypesConsumed">Event type ids the endpoint subscribes to.</param>
public sealed record EndpointSummary(
    string Id,
    string Name,
    string? Description,
    IReadOnlyList<string> EventTypesProduced,
    IReadOnlyList<string> EventTypesConsumed);
