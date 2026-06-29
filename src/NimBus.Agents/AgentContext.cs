namespace NimBus.Agents;

/// <summary>
/// The received event handed to an <see cref="IAgentHandler{TInput}"/>. Read-only data; the
/// handler returns an <see cref="AgentResult"/> rather than performing bus I/O directly.
/// </summary>
/// <typeparam name="TInput">The deserialized type of the source event payload.</typeparam>
public sealed class AgentContext<TInput>
{
    /// <summary>The deserialized event payload.</summary>
    public required TInput Input { get; init; }

    /// <summary>The raw event JSON, for fields not modelled on <typeparamref name="TInput"/>.</summary>
    public required string RawPayload { get; init; }

    /// <summary>The source event type id of the received event.</summary>
    public required string EventTypeId { get; init; }

    /// <summary>Handoff coordinates of the received event, echoed back when settling.</summary>
    public required HandoffCoordinates Coordinates { get; init; }
}
