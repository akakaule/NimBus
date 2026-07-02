namespace NimBus.Mcp.Http;

/// <summary>
/// Typed HTTP client interface for the NimBus WebApp Agent REST API.
/// All methods set the <c>X-Agent-Id</c> header (configured on the underlying <see cref="System.Net.Http.HttpClient"/>).
/// Non-success responses (400, 404, 409, etc.) throw <see cref="NimBusApiException"/>.
/// </summary>
public interface INimBusAgentApi
{
    /// <summary>GET /api/agent/catalog — returns registered endpoints and event types.</summary>
    Task<AgentCatalog?> GetCatalogAsync(CancellationToken ct = default);

    /// <summary>
    /// POST /api/agent/event-types — define or update an event type.
    /// Throws <see cref="NimBusApiException"/> on 409 (schema conflict with a different schema).
    /// </summary>
    Task<EventTypeInfo?> DefineEventTypeAsync(DefineEventTypeRequest req, CancellationToken ct = default);

    /// <summary>POST /api/agent/subscribe — subscribe the agent to an event type.</summary>
    Task SubscribeAsync(AgentSubscribeRequest req, CancellationToken ct = default);

    /// <summary>
    /// GET /api/agent/receive — poll for a pending handoff message.
    /// Returns <c>null</c> when the server responds 204 (no message available).
    /// </summary>
    Task<AgentReceivedMessage?> ReceiveAsync(string? eventTypeId, int? waitSeconds, CancellationToken ct = default);

    /// <summary>
    /// POST /api/agent/publish — publish an event.
    /// Throws <see cref="NimBusApiException"/> on 400 (schema validation failure) or 404 (unknown event type).
    /// </summary>
    Task PublishAsync(AgentPublishRequest req, CancellationToken ct = default);

    /// <summary>POST /api/agent/settle — settle a previously received handoff.</summary>
    Task SettleAsync(AgentSettleRequest req, CancellationToken ct = default);

    /// <summary>
    /// POST /api/messages/search + POST /api/audits/search — search for messages and audit entries.
    /// Sends <paramref name="query"/> as an eventTypeId hint (v1).
    /// Returns a combined JSON string with both results.
    /// </summary>
    Task<string> SearchFailuresAsync(string query, CancellationToken ct = default);
}
