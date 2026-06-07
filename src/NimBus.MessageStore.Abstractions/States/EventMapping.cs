using System;
using Newtonsoft.Json;

namespace NimBus.MessageStore.States;

/// <summary>Lifecycle state of an agent-authored event mapping (spec 023).</summary>
public enum MappingState
{
    Draft,
    Active,
    Paused,
    Stale,
    Rejected,
}

/// <summary>
/// An agent-authored, declarative transform from a source event type to a target event
/// type. Authored once by an agent, approved by an operator, then applied deterministically
/// by the Mapping Executor. Mirrors <see cref="EventSchema"/>'s storage shape.
/// </summary>
public class EventMapping
{
    /// <summary>Stable id: "{sourceEventTypeId}->{targetEventTypeId}". Cosmos partition key.</summary>
    [JsonProperty(PropertyName = "id")]
    public string Id { get; set; } = string.Empty;
    public string SourceEventTypeId { get; set; } = string.Empty;
    public string TargetEventTypeId { get; set; } = string.Empty;

    /// <summary>The reusable artifact: a JSONata expression mapping source JSON to target JSON.</summary>
    public string Transform { get; set; } = string.Empty;

    /// <summary>The LLM's short explanation, shown to the operator during review.</summary>
    public string? Rationale { get; set; }

    /// <summary>Serialized JSON array of { source, output } worked examples.</summary>
    public string? WorkedExamplesJson { get; set; }

    /// <summary>Fingerprint of the source schema at authoring time, for drift detection.</summary>
    public string SourceSchemaHash { get; set; } = string.Empty;

    public MappingState State { get; set; } = MappingState.Draft;
    public int Version { get; set; } = 1;

    public string? CreatedBy { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedUtc { get; set; }
}
