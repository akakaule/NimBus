namespace MappingAgent.Authoring;

/// <summary>
/// Produces a JSONata transform expression that maps a source event payload to a target
/// event payload. Two implementations: <see cref="ClaudeMappingAuthor"/> (LLM-based, requires
/// ANTHROPIC_API_KEY) and <see cref="DeterministicMappingAuthor"/> (heuristic, for CI /
/// local dev without a key).
/// </summary>
public interface IMappingAuthor
{
    /// <summary>
    /// Authors a JSONata transform and rationale for the given schemas and sample payloads.
    /// </summary>
    Task<MappingProposal> Author(AuthoringInput input, CancellationToken cancellationToken = default);
}

/// <summary>Input bundle passed to the author.</summary>
/// <param name="SourceEventTypeId">Wire id of the source event type.</param>
/// <param name="TargetEventTypeId">Wire id of the target event type.</param>
/// <param name="SourceSchemaJson">Full JSON Schema of the source event type.</param>
/// <param name="TargetSchemaJson">Full JSON Schema of the target event type.</param>
/// <param name="SampleSourcePayloads">Zero or more sample source payloads as JSON strings (from the bus history).</param>
public sealed record AuthoringInput(
    string SourceEventTypeId,
    string TargetEventTypeId,
    string SourceSchemaJson,
    string TargetSchemaJson,
    IReadOnlyList<string> SampleSourcePayloads);

/// <summary>The authored mapping proposal.</summary>
/// <param name="Transform">A JSONata expression mapping source JSON to target JSON.</param>
/// <param name="Rationale">Short explanation shown to the operator during review.</param>
public sealed record MappingProposal(string Transform, string Rationale);
