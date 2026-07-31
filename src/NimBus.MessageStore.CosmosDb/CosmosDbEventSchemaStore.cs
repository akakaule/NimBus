using Microsoft.Azure.Cosmos;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace NimBus.MessageStore;

/// <summary>
/// Cosmos DB implementation of <see cref="IEventSchemaStore"/>. Carved out of
/// <see cref="CosmosDbClient"/>, which keeps the container cache and exposes the
/// eventschemas container via the injected accessor.
/// </summary>
internal sealed class CosmosDbEventSchemaStore : IEventSchemaStore
{
    private readonly Func<Task<ICosmosContainerAdapter>> _getContainer;

    public CosmosDbEventSchemaStore(Func<Task<ICosmosContainerAdapter>> getContainer)
    {
        _getContainer = getContainer;
    }

    public async Task<EventSchema?> GetSchema(string eventTypeId)
    {
        var container = await _getContainer();
        try
        {
            var resp = await container.ReadItemAsync<EventSchema>(eventTypeId, new PartitionKey(eventTypeId));
            return resp.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<EventSchema>> GetSchemas()
    {
        var container = await _getContainer();
        var results = new List<EventSchema>();
        using var iterator = container.GetItemQueryIterator<EventSchema>("SELECT * FROM c");
        while (iterator.HasMoreResults)
            results.AddRange(await iterator.ReadNextAsync());
        return results;
    }

    public async Task<EventSchema> DefineEventType(EventSchema schema)
    {
        if (string.IsNullOrWhiteSpace(schema?.EventTypeId))
            throw new ArgumentException("schema.EventTypeId is required.", nameof(schema));
        if (string.IsNullOrWhiteSpace(schema?.JsonSchema))
            throw new ArgumentException("schema.JsonSchema is required.", nameof(schema));

        var existing = await GetSchema(schema.EventTypeId);
        if (existing != null)
        {
            if (!SchemaJson.Equal(existing.JsonSchema, schema.JsonSchema))
                throw new SchemaConflictException(schema.EventTypeId);
            return existing;
        }

        var container = await _getContainer();
        try
        {
            // Atomic create-or-409: never an upsert, so a concurrent create of a
            // DIFFERENT schema for the same new id can't silently overwrite.
            var resp = await container.CreateItemAsync(schema, new PartitionKey(schema.EventTypeId));
            return resp.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // Lost the create race. Re-read the winner; surface a conflict only if it
            // differs (schemas are immutable), otherwise the create was idempotent.
            var raced = await GetSchema(schema.EventTypeId);
            if (raced is null)
                throw new InvalidOperationException(
                    $"Event type '{schema.EventTypeId}' reported a create conflict but could not be re-read.");
            if (!SchemaJson.Equal(raced.JsonSchema, schema.JsonSchema))
                throw new SchemaConflictException(schema.EventTypeId);
            return raced;
        }
    }
}
