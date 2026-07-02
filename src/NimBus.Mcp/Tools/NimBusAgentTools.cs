using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using NimBus.Mcp.Http;

namespace NimBus.Mcp.Tools;

/// <summary>
/// MCP tools that expose NimBus agent capabilities to LLMs.
/// Each tool is a thin delegate to <see cref="INimBusAgentApi"/>; all business
/// logic lives in the REST layer behind that interface.
/// </summary>
/// <remarks>
/// Constructor parameters are injected by the MCP SDK via
/// <c>ActivatorUtilities.CreateInstance</c> using the per-request
/// <see cref="IServiceProvider"/>.  Primitive method parameters become
/// the tool's LLM-facing input schema.
/// </remarks>
[McpServerToolType]
public sealed class NimBusAgentTools
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly INimBusAgentApi _api;

    /// <summary>Constructs the tool class; <paramref name="api"/> is resolved from DI.</summary>
    public NimBusAgentTools(INimBusAgentApi api)
    {
        _api = api;
    }

    // ── 1. discover_topology ─────────────────────────────────────────────────

    /// <summary>Returns all registered NimBus endpoints and event types as JSON.</summary>
    [McpServerTool(Name = "discover_topology")]
    [Description("Returns the full NimBus topology: registered endpoints and all known event types with their schemas. Call this first to understand what events exist before publishing or subscribing.")]
    public async Task<string> DiscoverTopologyAsync(CancellationToken ct = default)
    {
        var catalog = await _api.GetCatalogAsync(ct).ConfigureAwait(false);
        if (catalog is null)
        {
            return "no topology available";
        }

        return JsonSerializer.Serialize(catalog, s_json);
    }

    // ── 2. define_event_type ─────────────────────────────────────────────────

    /// <summary>Defines (registers or updates) a NimBus event type.</summary>
    [McpServerTool(Name = "define_event_type")]
    [Description("Registers a new event type in NimBus or updates an existing one whose schema matches. Returns the stored EventTypeInfo as JSON. If the event type already exists with a DIFFERENT schema a conflict is returned instead of an error — inspect the message and decide whether to update the schema or use the existing definition.")]
    public async Task<string> DefineEventTypeAsync(
        [Description("Unique event-type identifier, e.g. 'order.created'.")] string eventTypeId,
        [Description("JSON Schema string that describes the event payload shape, e.g. '{\"type\":\"object\",\"properties\":{}}'.")] string jsonSchema,
        [Description("Optional human-readable name for the event type.")] string? name = null,
        [Description("Optional description of when and why this event is published.")] string? description = null,
        [Description("Optional JSONPath expression that identifies the session key in the payload, e.g. '$.orderId'.")] string? sessionKeyPath = null,
        CancellationToken ct = default)
    {
        try
        {
            var req = new DefineEventTypeRequest(eventTypeId, jsonSchema, name, description, sessionKeyPath);
            var info = await _api.DefineEventTypeAsync(req, ct).ConfigureAwait(false);
            if (info is null)
            {
                return "event type defined (no result returned)";
            }

            return JsonSerializer.Serialize(info, s_json);
        }
        catch (NimBusApiException ex) when (ex.StatusCode == 409)
        {
            return $"conflict: event type '{eventTypeId}' already exists with a different schema. " +
                   $"Use discover_topology to retrieve the current schema, then decide whether to adjust your schema or reuse the existing definition. " +
                   $"Server detail: {ex.Body}";
        }
    }

    // ── 3. subscribe ─────────────────────────────────────────────────────────

    /// <summary>Subscribes the agent to a NimBus event type so messages are routed to it.</summary>
    [McpServerTool(Name = "subscribe")]
    [Description("Subscribes this agent to the specified event type. After subscribing, messages of that type will be queued for this agent and can be retrieved with receive_messages.")]
    public async Task<string> SubscribeAsync(
        [Description("The event-type identifier to subscribe to, e.g. 'order.created'.")] string eventTypeId,
        CancellationToken ct = default)
    {
        await _api.SubscribeAsync(new AgentSubscribeRequest(eventTypeId), ct).ConfigureAwait(false);
        return "subscribed";
    }

    // ── 4. receive_messages ──────────────────────────────────────────────────

    /// <summary>Polls for a pending handoff message from NimBus.</summary>
    [McpServerTool(Name = "receive_messages")]
    [Description("Polls for the next pending message routed to this agent. Returns the message as JSON (including payload and HandoffCoordinates needed for settle_message), or 'no message available' when the queue is empty. Use waitSeconds to long-poll instead of tight-looping.")]
    public async Task<string> ReceiveMessagesAsync(
        [Description("Optional: filter to messages of this event type only.")] string? eventTypeId = null,
        [Description("Optional: seconds to wait for a message (long-poll). 0 or omit for immediate check. Max ~30 seconds recommended.")] int? waitSeconds = null,
        CancellationToken ct = default)
    {
        var message = await _api.ReceiveAsync(eventTypeId, waitSeconds, ct).ConfigureAwait(false);
        if (message is null)
        {
            return "no message available";
        }

        return JsonSerializer.Serialize(message, s_json);
    }

    // ── 5. publish_event ─────────────────────────────────────────────────────

    /// <summary>Publishes an event to NimBus.</summary>
    [McpServerTool(Name = "publish_event")]
    [Description("Publishes an event to NimBus for routing to downstream subscribers. The payload must conform to the event type's registered JSON Schema. Returns 'published' on success, or an error message when the payload fails validation (400) or the event type does not exist (404) — inspect the error and fix the payload or event type before retrying.")]
    public async Task<string> PublishEventAsync(
        [Description("The event-type identifier to publish, e.g. 'order.created'.")] string eventTypeId,
        [Description("The event payload as a JSON string conforming to the event type's schema.")] string payload,
        [Description("Optional session identifier for ordered processing. Required when the event type has a sessionKeyPath defined.")] string? sessionId = null,
        CancellationToken ct = default)
    {
        try
        {
            await _api.PublishAsync(new AgentPublishRequest(eventTypeId, payload, sessionId), ct).ConfigureAwait(false);
            return "published";
        }
        catch (NimBusApiException ex) when (ex.StatusCode is 400 or 404)
        {
            return $"error ({ex.StatusCode}): {ex.Body}";
        }
    }

    // ── 6. settle_message ────────────────────────────────────────────────────

    /// <summary>Settles (completes or fails) a previously received handoff message.</summary>
    [McpServerTool(Name = "settle_message")]
    [Description("Settles a previously received handoff message, marking it as completed or failed. The six coordinate fields (eventId, sessionId, messageId, eventTypeId, correlationId, originatingMessageId) must come verbatim from the HandoffCoordinates in the received message. outcome must be 'complete' or 'fail'. Provide result (JSON string) for success or errorText/errorType for failures.")]
    public async Task<string> SettleMessageAsync(
        [Description("EventId from the received message's HandoffCoordinates.")] string eventId,
        [Description("SessionId from the received message's HandoffCoordinates.")] string sessionId,
        [Description("MessageId from the received message's HandoffCoordinates.")] string messageId,
        [Description("EventTypeId from the received message's HandoffCoordinates.")] string eventTypeId,
        [Description("CorrelationId from the received message's HandoffCoordinates.")] string correlationId,
        [Description("OriginatingMessageId from the received message's HandoffCoordinates.")] string originatingMessageId,
        [Description("Settlement outcome: 'complete' to mark the message as successfully processed, 'fail' to mark it as failed.")] string outcome,
        [Description("Optional result payload as a JSON string (used when outcome is 'complete').")] string? result = null,
        [Description("Optional human-readable error description (used when outcome is 'fail').")] string? errorText = null,
        [Description("Optional error type/category string (used when outcome is 'fail').")] string? errorType = null,
        CancellationToken ct = default)
    {
        var coordinates = new HandoffCoordinates(
            eventId,
            sessionId,
            messageId,
            eventTypeId,
            correlationId,
            originatingMessageId);

        var req = new AgentSettleRequest(coordinates, outcome, result, errorText, errorType);
        await _api.SettleAsync(req, ct).ConfigureAwait(false);
        return "settled";
    }

    // ── 7. search_failures ───────────────────────────────────────────────────

    /// <summary>Searches NimBus message and audit records for failures or specific events.</summary>
    [McpServerTool(Name = "search_failures")]
    [Description("Searches both message tracking and audit records in NimBus. Pass an event type ID or keyword as the query. Returns a combined JSON object with 'messages' and 'audits' arrays. Useful for diagnosing stuck or failed messages.")]
    public async Task<string> SearchFailuresAsync(
        [Description("The search query — typically an event type ID (e.g. 'order.created') or a keyword to filter results.")] string query,
        CancellationToken ct = default)
    {
        return await _api.SearchFailuresAsync(query, ct).ConfigureAwait(false);
    }
}
