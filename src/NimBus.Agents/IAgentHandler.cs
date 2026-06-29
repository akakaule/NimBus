namespace NimBus.Agents;

/// <summary>
/// Implemented by an agent author. The SDK deserializes the parked event JSON into
/// <typeparamref name="TInput"/>, invokes <see cref="HandleAsync"/>, then acts on the
/// returned <see cref="AgentResult"/> — it publishes the requested events and settles the
/// handoff. The body is arbitrary: call an LLM, run deterministic logic, or invoke another
/// service. The handler performs no bus I/O itself, which keeps it pure and unit-testable.
/// </summary>
/// <typeparam name="TInput">The deserialized type of the source event payload.</typeparam>
public interface IAgentHandler<TInput>
{
    /// <summary>Processes one received event and returns the desired outcome.</summary>
    /// <param name="context">The received event: deserialized input plus handoff coordinates.</param>
    /// <param name="cancellationToken">Cancelled on host shutdown.</param>
    /// <returns>What the SDK should publish, and how to settle the handoff.</returns>
    Task<AgentResult> HandleAsync(AgentContext<TInput> context, CancellationToken cancellationToken);
}
