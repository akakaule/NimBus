using NimBus.MessageStore.States;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Registry of agent-defined event-type schemas (spec 022). Schemas are immutable in v1.
/// </summary>
public interface IEventSchemaStore
{
    /// <summary>Returns the schema for an event type, or null if unknown.</summary>
    Task<EventSchema?> GetSchema(string eventTypeId);

    /// <summary>Returns all registered schemas (for discovery / catalog).</summary>
    Task<IReadOnlyList<EventSchema>> GetSchemas();

    /// <summary>
    /// Registers an event type. Idempotent when the stored schema is byte-identical (returns
    /// the existing record); throws <see cref="SchemaConflictException"/> when an event type
    /// already exists with a different schema.
    /// </summary>
    Task<EventSchema> DefineEventType(EventSchema schema);
}
