#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.MessageStore.CosmosDb.Tests;

/// <summary>
/// Pure unit tests for <see cref="CosmosDbClient"/> backed by fake adapters.
/// Unlike the conformance suite these run without a live Cosmos account.
/// </summary>
[TestClass]
public sealed class CosmosDbClientUnitTests
{
    [TestMethod]
    public async Task DownloadEndpointStateCount_stamps_EventTime_in_utc()
    {
        var client = new CosmosDbClient(new FakeCosmosClientAdapter());

        var state = await client.DownloadEndpointStateCount("endpoint-1");

        Assert.AreEqual(DateTimeKind.Utc, state.EventTime.Kind,
            "EventTime must be UTC — the SQL Server and in-memory stores stamp UtcNow; local time skews cross-provider comparisons.");
        Assert.IsTrue(Math.Abs((DateTime.UtcNow - state.EventTime).TotalMinutes) < 1.0,
            $"EventTime {state.EventTime:O} should be within a minute of DateTime.UtcNow.");
    }

    [TestMethod]
    public async Task GetEndpointContainer_caches_handle_so_ensure_exists_runs_once_per_endpoint()
    {
        var adapter = new FakeCosmosClientAdapter();
        var client = new CosmosDbClient(adapter);

        // Repeated operations on the same endpoint should resolve the container
        // once; the cached handle skips the control-plane round-trip after.
        for (var i = 0; i < 3; i++)
        {
            await client.DownloadEndpointStateCount("endpoint-1");
        }

        Assert.AreEqual(1, adapter.Database.GetCreateContainerCallCount("endpoint-1"),
            "CreateContainerIfNotExistsAsync must run once per container; subsequent accesses reuse the cached handle.");
    }

    [TestMethod]
    public async Task PurgeMessages_evicts_cached_handle_so_next_access_recreates_container()
    {
        var adapter = new FakeCosmosClientAdapter();
        var client = new CosmosDbClient(adapter);

        await client.DownloadEndpointStateCount("endpoint-1");

        // Purge deletes the container and must evict the cache, so the next
        // access re-runs "ensure exists" instead of reusing a stale handle.
        var purged = await client.PurgeMessages("endpoint-1");
        await client.DownloadEndpointStateCount("endpoint-1");

        Assert.IsTrue(purged, "PurgeMessages should succeed against the fake adapter.");
        Assert.AreEqual(2, adapter.Database.GetCreateContainerCallCount("endpoint-1"),
            "After a purge the cached handle is stale; the next access must re-ensure the container exists.");
    }

    [TestMethod]
    public async Task GetEndpointContainer_does_not_cache_faulted_creation_so_next_access_retries()
    {
        var adapter = new FakeCosmosClientAdapter();
        adapter.Database.CreateFailuresRemaining = 1;
        var client = new CosmosDbClient(adapter);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => client.DownloadEndpointStateCount("endpoint-1"));

        // The faulted creation must not be cached — the retry should run
        // "ensure exists" again and succeed.
        await client.DownloadEndpointStateCount("endpoint-1");

        Assert.AreEqual(2, adapter.Database.GetCreateContainerCallCount("endpoint-1"),
            "A failed creation must be evicted so the next caller retries CreateContainerIfNotExistsAsync.");
    }

    private sealed class FakeCosmosClientAdapter : ICosmosClientAdapter
    {
        public FakeDatabaseAdapter Database { get; } = new FakeDatabaseAdapter();

        public ICosmosDatabaseAdapter GetDatabase(string id) => Database;
    }

    private sealed class FakeDatabaseAdapter : ICosmosDatabaseAdapter
    {
        private readonly Dictionary<string, int> _createContainerCalls = new(StringComparer.Ordinal);

        public int CreateFailuresRemaining { get; set; }

        public int GetCreateContainerCallCount(string id) => _createContainerCalls.GetValueOrDefault(id);

        public ICosmosContainerAdapter GetContainer(string id) => new FakeContainerAdapter();

        public Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(string id, string partitionKeyPath)
        {
            _createContainerCalls[id] = _createContainerCalls.GetValueOrDefault(id) + 1;
            if (CreateFailuresRemaining > 0)
            {
                CreateFailuresRemaining--;
                throw new InvalidOperationException("Simulated container creation failure.");
            }

            return Task.FromResult<ICosmosContainerAdapter>(new FakeContainerAdapter());
        }
    }

    private sealed class FakeContainerResponse : ContainerResponse
    {
    }

    private sealed class FakeContainerAdapter : ICosmosContainerAdapter
    {
        public FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition)
            => new EmptyFeedIterator<T>();

        public FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
            => new EmptyFeedIterator<T>();

        public FeedIterator<T> GetItemQueryIterator<T>(string queryText)
            => new EmptyFeedIterator<T>();

        public FeedIterator<T> GetItemQueryIterator<T>(string queryText, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
            => new EmptyFeedIterator<T>();

        public IOrderedQueryable<T> GetItemLinqQueryable<T>(bool allowSynchronousQueryExecution = false, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
            => throw new NotSupportedException();

        public Task<ItemResponse<T>> UpsertItemAsync<T>(T item, PartitionKey partitionKey = default)
            => throw new NotSupportedException();

        public Task<ItemResponse<T>> DeleteItemAsync<T>(string id, PartitionKey partitionKey)
            => throw new NotSupportedException();

        public Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey)
            => throw new NotSupportedException();

        public Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey, ItemRequestOptions requestOptions)
            => throw new NotSupportedException();

        public Task<ItemResponse<T>> PatchItemAsync<T>(string id, PartitionKey partitionKey, IReadOnlyList<PatchOperation> patchOperations)
            => throw new NotSupportedException();

        public Task<ContainerResponse> DeleteContainerAsync()
            => Task.FromResult<ContainerResponse>(new FakeContainerResponse());

        public Task<FeedResponse<T>> ReadManyItemsAsync<T>(IReadOnlyList<(string id, PartitionKey partitionKey)> items)
            => throw new NotSupportedException();
    }

    private sealed class EmptyFeedIterator<T> : FeedIterator<T>
    {
        public override bool HasMoreResults => false;

        public override Task<FeedResponse<T>> ReadNextAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Iterator is empty.");
    }
}
