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

    private sealed class FakeCosmosClientAdapter : ICosmosClientAdapter
    {
        public ICosmosDatabaseAdapter GetDatabase(string id) => new FakeDatabaseAdapter();
    }

    private sealed class FakeDatabaseAdapter : ICosmosDatabaseAdapter
    {
        public ICosmosContainerAdapter GetContainer(string id) => new FakeContainerAdapter();

        public Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(string id, string partitionKeyPath)
            => Task.FromResult<ICosmosContainerAdapter>(new FakeContainerAdapter());
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
            => throw new NotSupportedException();

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
