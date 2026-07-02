using System.Text.Json.Serialization;

namespace NimBus.Mcp.Http;

// All records use System.Text.Json with JsonSerializerDefaults.Web (camelCase property names).
// payload / jsonSchema / result fields are raw JSON strings — they are passed through verbatim.

/// <summary>Catalog of endpoints and event types registered in NimBus.</summary>
public sealed record AgentCatalog(
    string[] Endpoints,
    EventTypeInfo[] EventTypes);

/// <summary>Describes a registered NimBus event type.</summary>
public sealed record EventTypeInfo(
    string EventTypeId,
    string? Name,
    string? JsonSchema,
    string? Description);

/// <summary>Request to define (register/update) an event type.</summary>
public sealed record DefineEventTypeRequest(
    string EventTypeId,
    string JsonSchema,
    string? Name,
    string? Description,
    string? SessionKeyPath);

/// <summary>Request to subscribe the agent to an event type.</summary>
public sealed record AgentSubscribeRequest(
    string EventTypeId);

/// <summary>Addressing coordinates for a received handoff message.</summary>
public sealed record HandoffCoordinates(
    string EventId,
    string SessionId,
    string MessageId,
    string EventTypeId,
    string CorrelationId,
    string OriginatingMessageId);

/// <summary>A message received from a NimBus handoff.</summary>
public sealed record AgentReceivedMessage(
    string EventTypeId,
    string Payload,
    HandoffCoordinates Coordinates);

/// <summary>Request to publish an event to NimBus.</summary>
public sealed record AgentPublishRequest(
    string EventTypeId,
    string Payload,
    string? SessionId);

/// <summary>Request to settle (complete/fail) a previously received handoff.</summary>
public sealed record AgentSettleRequest(
    HandoffCoordinates Coordinates,
    string Outcome,
    string? Result,
    string? ErrorText,
    string? ErrorType);

// Search request shapes — minimal bodies sent to /api/messages/search and /api/audits/search.
// The server accepts a richer filter structure; this MCP adapter sends only freeText via eventTypeId
// field for v1. A later phase can expose richer filter params as tool arguments.

/// <summary>Minimal search filter sent to /api/messages/search (POST).</summary>
internal sealed record MessageSearchBody(
    [property: JsonPropertyName("filter")] MessageSearchFilterBody Filter);

/// <summary>Filter for message search — v1 passes eventTypeId as a freetext hint.</summary>
internal sealed record MessageSearchFilterBody(
    [property: JsonPropertyName("eventTypeId")] string[]? EventTypeId);

/// <summary>Minimal search body sent to /api/audits/search (POST).</summary>
internal sealed record AuditSearchBody(
    [property: JsonPropertyName("filter")] AuditSearchFilterBody Filter);

/// <summary>Filter for audit search — v1 passes eventTypeId string.</summary>
internal sealed record AuditSearchFilterBody(
    [property: JsonPropertyName("eventTypeId")] string? EventTypeId);

/// <summary>
/// Raised when the NimBus REST API returns a non-success status code.
/// The <see cref="StatusCode"/> and <see cref="Body"/> give the LLM actionable details.
/// </summary>
public sealed class NimBusApiException : Exception
{
    /// <summary>HTTP status code returned by the server.</summary>
    public int StatusCode { get; }

    /// <summary>Response body text returned by the server.</summary>
    public string Body { get; }

    /// <inheritdoc/>
    public NimBusApiException(int statusCode, string body)
        : base($"NimBus API returned {statusCode}: {body}")
    {
        StatusCode = statusCode;
        Body = body;
    }
}
