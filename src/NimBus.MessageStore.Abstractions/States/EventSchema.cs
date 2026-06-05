using Newtonsoft.Json;
using System;

namespace NimBus.MessageStore.States;

/// <summary>One agent-defined event-type contract (spec 022). Immutable after creation.</summary>
public class EventSchema
{
    /// <summary>Namespaced, globally-unique id, e.g. "crm.contact.enriched.v1". Cosmos partition key.</summary>
    [JsonProperty(PropertyName = "id")] public string EventTypeId { get; set; }

    public string Name { get; set; }

    /// <summary>JSON Schema describing the payload, as a JSON string.</summary>
    public string JsonSchema { get; set; }

    public string? Description { get; set; }

    /// <summary>Optional JSONPath selecting the session key for ordering.</summary>
    public string? SessionKeyPath { get; set; }

    public int Version { get; set; } = 1;

    /// <summary>The agent that created it (from the API key).</summary>
    public string AgentId { get; set; }

    public string? CreatedBy { get; set; }

    public DateTime CreatedUtc { get; set; }
}
