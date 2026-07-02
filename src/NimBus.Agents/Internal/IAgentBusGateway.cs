namespace NimBus.Agents.Internal;

/// <summary>Typed wrapper over the NimBus <c>/api/agent/*</c> REST surface.</summary>
internal interface IAgentBusGateway
{
    /// <summary>Registers the agent's interest in <paramref name="eventTypeId"/> for server-side filtering.</summary>
    Task SubscribeAsync(string eventTypeId, CancellationToken ct);

    /// <summary>Defines (or confirms) an event type and its schema. A 409 (already defined with a
    /// different schema) is swallowed.</summary>
    Task DefineEventTypeAsync(string eventTypeId, string jsonSchema, string? name, string? description, string? sessionKeyPath, CancellationToken ct);

    /// <summary>Long-polls for the next parked event. Returns <c>null</c> when nothing is parked (HTTP 204).</summary>
    Task<AgentReceivedMessage?> ReceiveAsync(string? eventTypeId, int waitSeconds, CancellationToken ct);

    /// <summary>Publishes an event whose body is validated against the type's schema server-side.</summary>
    Task PublishAsync(string eventTypeId, string payloadJson, string? sessionId, CancellationToken ct);

    /// <summary>Settles a received handoff complete (<paramref name="success"/> true) or fail.</summary>
    Task SettleAsync(HandoffCoordinates coordinates, bool success, string? result, string? errorText, string? errorType, CancellationToken ct);
}
