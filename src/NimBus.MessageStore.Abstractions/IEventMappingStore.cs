using System.Collections.Generic;
using System.Threading.Tasks;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Registry of agent-authored event mappings (spec 023). Mirrors <see cref="IEventSchemaStore"/>.
/// Unlike schemas, mappings are mutable across their lifecycle (Draft->Active->...->re-author).
/// </summary>
public interface IEventMappingStore
{
    /// <summary>Returns the mapping by id, or null if unknown.</summary>
    Task<EventMapping?> GetMapping(string id);

    /// <summary>The Executor's hot lookup: the single Active mapping for a source type, or null.</summary>
    Task<EventMapping?> GetActiveMappingForSource(string sourceEventTypeId);

    /// <summary>All mappings (for the review UI / list API).</summary>
    Task<IReadOnlyList<EventMapping>> GetMappings();

    /// <summary>Upsert by <see cref="EventMapping.Id"/>. Returns the stored record.</summary>
    Task<EventMapping> SaveMapping(EventMapping mapping);
}
