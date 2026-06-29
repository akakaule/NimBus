namespace NimBus.Agents;

/// <summary>
/// Configuration for a hosted agent registered via
/// <see cref="AgentServiceCollectionExtensions.AddNimBusAgent{THandler, TInput}"/>.
/// </summary>
public sealed class AgentOptions
{
    /// <summary>Agent identity sent as the <c>X-Agent-Id</c> header on every request. Required.</summary>
    public string AgentId { get; set; } = "";

    /// <summary>
    /// Base address of the NimBus agent REST API (nimbus-ops). Defaults to the Aspire
    /// service-discovery scheme <c>https+http://nimbus-ops</c>; set an absolute URL for
    /// non-Aspire hosts.
    /// </summary>
    public string BaseAddress { get; set; } = "https+http://nimbus-ops";

    /// <summary>
    /// Long-poll window (seconds) for the receive endpoint. Must be 0..60 and should stay below
    /// ~10s (the standard-resilience per-attempt timeout applied by <c>AddServiceDefaults</c>).
    /// Defaults to 5.
    /// </summary>
    public int ReceiveWaitSeconds { get; set; } = 5;

    /// <summary>Delay after a failed loop iteration before retrying. Defaults to 1 second.</summary>
    public TimeSpan ErrorBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The source event type the agent subscribes to and receives. Set via <see cref="Subscribe"/>.</summary>
    public string? SourceEventTypeId { get; private set; }

    /// <summary>Output event types to define (idempotently) on startup.</summary>
    internal List<OutputEventTypeRegistration> Outputs { get; } = new();

    /// <summary>Subscribe to and receive the given source event type (single source in v1).</summary>
    /// <param name="eventTypeId">The source event type id.</param>
    /// <returns>This instance, for chaining.</returns>
    public AgentOptions Subscribe(string eventTypeId)
    {
        SourceEventTypeId = eventTypeId;
        return this;
    }

    /// <summary>Declare an output event type to define (idempotently) on startup.</summary>
    /// <param name="eventTypeId">The output event type id.</param>
    /// <param name="jsonSchema">The JSON Schema for the output event.</param>
    /// <param name="name">Optional human-readable name.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="sessionKeyPath">Optional JSON path to the session key within the payload.</param>
    /// <returns>This instance, for chaining.</returns>
    public AgentOptions DefineOutput(string eventTypeId, string jsonSchema, string? name = null, string? description = null, string? sessionKeyPath = null)
    {
        Outputs.Add(new OutputEventTypeRegistration(eventTypeId, jsonSchema, name, description, sessionKeyPath));
        return this;
    }
}

internal sealed record OutputEventTypeRegistration(
    string EventTypeId,
    string JsonSchema,
    string? Name,
    string? Description,
    string? SessionKeyPath);
