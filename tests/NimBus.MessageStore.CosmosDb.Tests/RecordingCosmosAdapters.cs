#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NimBus.MessageStore.CosmosDb.Tests;

/// <summary>
/// Shared test double for the Cosmos adapter chain. Records every container creation
/// (and <em>which</em> overload created it), every upserted document and every patch, so
/// tests can assert on the wire shape the store produces without a live account.
/// One container instance per id, so a test can tell an endpoint container apart from a
/// shared one.
/// </summary>
internal sealed class RecordingCosmosClientAdapter : ICosmosClientAdapter, ICosmosDatabaseAdapter
{
    private readonly Dictionary<string, RecordingCosmosContainerAdapter> _containers = new(StringComparer.Ordinal);

    /// <summary>Every container creation in order: the id, and the properties when the
    /// <see cref="ContainerProperties"/> overload was used (null when the id/partition-key one was).</summary>
    public List<(string Id, ContainerProperties? Properties)> ContainerCreations { get; } = new();

    public ICosmosDatabaseAdapter GetDatabase(string id) => this;

    public ICosmosContainerAdapter GetContainer(string id) => Container(id);

    public RecordingCosmosContainerAdapter Container(string id)
    {
        if (!_containers.TryGetValue(id, out var container))
        {
            container = new RecordingCosmosContainerAdapter();
            _containers[id] = container;
        }

        return container;
    }

    /// <summary>The properties a container was created with, or null when it was created
    /// through the id/partition-key overload. Throws when the container was never created.</summary>
    public ContainerProperties? CreationPropertiesFor(string id) =>
        ContainerCreations.Single(c => string.Equals(c.Id, id, StringComparison.Ordinal)).Properties;

    public bool WasCreated(string id) =>
        ContainerCreations.Exists(c => string.Equals(c.Id, id, StringComparison.Ordinal));

    public Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(string id, string partitionKeyPath)
    {
        ContainerCreations.Add((id, null));
        return Task.FromResult<ICosmosContainerAdapter>(Container(id));
    }

    public Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(
        ContainerProperties containerProperties,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(containerProperties);
        ContainerCreations.Add((containerProperties.Id, containerProperties));
        return Task.FromResult<ICosmosContainerAdapter>(Container(containerProperties.Id));
    }
}

/// <summary>
/// A database adapter that predates the <see cref="ContainerProperties"/> overload — it
/// implements only the original id/partition-key method, so the fail-closed default
/// interface implementation is what runs.
/// </summary>
internal sealed class LegacyCosmosClientAdapter : ICosmosClientAdapter, ICosmosDatabaseAdapter
{
    private readonly RecordingCosmosContainerAdapter _container = new();

    public List<string> ContainerCreations { get; } = new();

    public ICosmosDatabaseAdapter GetDatabase(string id) => this;

    public ICosmosContainerAdapter GetContainer(string id) => _container;

    public Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(string id, string partitionKeyPath)
    {
        ContainerCreations.Add(id);
        return Task.FromResult<ICosmosContainerAdapter>(_container);
    }
}

internal sealed class RecordingCosmosContainerAdapter : ICosmosContainerAdapter
{
    /// <summary>Every upserted document, in order.</summary>
    public List<object?> UpsertedItems { get; } = new();

    public List<ItemRequestOptions?> CapturedRequestOptions { get; } = new();

    /// <summary>Every patch, in order.</summary>
    public List<IReadOnlyList<PatchOperation>> CapturedPatches { get; } = new();

    public int QueryCount { get; private set; }

    /// <summary>The upserted document as it goes on the wire. <c>EventDbo</c> is private to
    /// <c>CosmosDbClient</c>, so serializing is the only way to read its <c>ttl</c>.</summary>
    public JObject UpsertedDocument(int index) =>
        JObject.Parse(JsonConvert.SerializeObject(UpsertedItems[index]));

    public JObject SingleUpsertedDocument() =>
        JObject.Parse(JsonConvert.SerializeObject(UpsertedItems.Single()));

    public FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition)
    {
        QueryCount++;
        return new EmptyFeedIterator<T>();
    }

    public FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
    {
        QueryCount++;
        return new EmptyFeedIterator<T>();
    }

    public FeedIterator<T> GetItemQueryIterator<T>(string queryText)
    {
        QueryCount++;
        return new EmptyFeedIterator<T>();
    }

    public FeedIterator<T> GetItemQueryIterator<T>(string queryText, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
    {
        QueryCount++;
        return new EmptyFeedIterator<T>();
    }

    public IOrderedQueryable<T> GetItemLinqQueryable<T>(bool allowSynchronousQueryExecution = false, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
        => throw new NotSupportedException();

    public Task<ItemResponse<T>> UpsertItemAsync<T>(T item, PartitionKey partitionKey = default, ItemRequestOptions? requestOptions = null)
    {
        UpsertedItems.Add(item);
        CapturedRequestOptions.Add(requestOptions);
        // The client under test is constructed without a logger, so the null-conditional
        // trace log never dereferences the (null) response.
        return Task.FromResult<ItemResponse<T>>(null!);
    }

    public Task<ItemResponse<T>> PatchItemAsync<T>(string id, PartitionKey partitionKey, IReadOnlyList<PatchOperation> patchOperations)
    {
        CapturedPatches.Add(patchOperations);
        return Task.FromResult<ItemResponse<T>>(null!);
    }

    public Task<ItemResponse<T>> CreateItemAsync<T>(T item, PartitionKey partitionKey = default)
        => throw new NotSupportedException();

    public Task<ItemResponse<T>> DeleteItemAsync<T>(string id, PartitionKey partitionKey)
        => throw new NotSupportedException();

    public Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey)
        => throw new NotSupportedException();

    public Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey, ItemRequestOptions requestOptions)
        => throw new NotSupportedException();

    public Task<ContainerResponse> DeleteContainerAsync()
        => throw new NotSupportedException();

    public Task<FeedResponse<T>> ReadManyItemsAsync<T>(IReadOnlyList<(string id, PartitionKey partitionKey)> items)
        => throw new NotSupportedException();
}

internal sealed class EmptyFeedIterator<T> : FeedIterator<T>
{
    public override bool HasMoreResults => false;

    public override Task<FeedResponse<T>> ReadNextAsync(CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Iterator is empty.");
}
