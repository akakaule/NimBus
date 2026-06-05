namespace EnrichmentAgent.Bus;

/// <summary>
/// Seam over the NimBus agent REST surface (Phase 1, hosted on the WebApp /
/// <c>nimbus-ops</c> resource). Keeps the agent loop transport-agnostic so a
/// future MCP-client implementation can be swapped in without touching
/// <see cref="AgentLoopWorker"/>. The v1 implementation is
/// <see cref="RestBusGateway"/>, which calls the REST endpoints directly.
/// </summary>
public interface IBusGateway
{
    /// <summary>Registers this agent's interest in <paramref name="eventTypeId"/> so
    /// later <see cref="ReceiveAsync"/> calls can be server-side filtered.</summary>
    Task SubscribeAsync(string eventTypeId, CancellationToken ct = default);

    /// <summary>Defines (or confirms) an event type and its JSON schema. Idempotent:
    /// re-defining the same id with the same schema is a no-op. A 409 (the id already
    /// exists with a <em>different</em> schema) is tolerated/swallowed here — callers
    /// pass a fixed schema, so this only happens across stale runs and is harmless.</summary>
    Task DefineEventTypeAsync(string eventTypeId, string jsonSchema, string? name, CancellationToken ct = default);

    /// <summary>Long-polls for the next parked event of <paramref name="eventTypeId"/>
    /// (or any subscribed type when null). Returns <c>null</c> when nothing is parked
    /// (HTTP 204).</summary>
    Task<ReceivedMessage?> ReceiveAsync(string? eventTypeId, int waitSeconds, CancellationToken ct = default);

    /// <summary>Publishes an event. <paramref name="payloadJson"/> is the event body as a
    /// JSON string, validated server-side against the event type's schema.</summary>
    Task PublishAsync(string eventTypeId, string payloadJson, string? sessionId, CancellationToken ct = default);

    /// <summary>Settles a previously received handoff — <paramref name="outcome"/> is
    /// <c>"complete"</c> or <c>"fail"</c>.</summary>
    Task SettleAsync(HandoffCoordinates coordinates, string outcome, string? result, CancellationToken ct = default);
}

/// <summary>A parked event handed to the agent by <see cref="IBusGateway.ReceiveAsync"/>.</summary>
/// <param name="EventTypeId">The event type id of the parked event.</param>
/// <param name="Payload">The event body as a JSON string.</param>
/// <param name="Coordinates">Handoff coordinates required to settle the event.</param>
public sealed record ReceivedMessage(string EventTypeId, string Payload, HandoffCoordinates Coordinates);

/// <summary>Opaque coordinates identifying a parked handoff, echoed back on settle.</summary>
public sealed record HandoffCoordinates(
    string EventId,
    string SessionId,
    string MessageId,
    string EventTypeId,
    string CorrelationId,
    string OriginatingMessageId);
