namespace NimBus.Agents;

/// <summary>Opaque coordinates identifying a parked handoff, echoed back when settling.</summary>
/// <param name="EventId">The parked event's id.</param>
/// <param name="SessionId">The session the event belongs to.</param>
/// <param name="MessageId">The underlying message id.</param>
/// <param name="EventTypeId">The event type id.</param>
/// <param name="CorrelationId">The correlation id.</param>
/// <param name="OriginatingMessageId">The originating message id.</param>
public sealed record HandoffCoordinates(
    string EventId,
    string SessionId,
    string MessageId,
    string EventTypeId,
    string CorrelationId,
    string OriginatingMessageId);

/// <summary>A parked event returned by the agent REST receive endpoint.</summary>
/// <param name="EventTypeId">The event type id of the parked event.</param>
/// <param name="Payload">The event body as a JSON string.</param>
/// <param name="Coordinates">Handoff coordinates required to settle the event.</param>
internal sealed record AgentReceivedMessage(string EventTypeId, string Payload, HandoffCoordinates Coordinates);
