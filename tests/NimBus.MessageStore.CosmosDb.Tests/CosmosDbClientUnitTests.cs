#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.States;

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
    public async Task SetEndpointMetadata_reports_throttled_upserts_through_logger()
    {
        var logger = new CapturingLogger();
        var container = new FakeContainerAdapter
        {
            UpsertException = new CosmosException("throttled", HttpStatusCode.TooManyRequests, 0, "activity-1", 0),
        };
        var client = new CosmosDbClient(new FakeCosmosClientAdapter(container), logger);

        await Assert.ThrowsExceptionAsync<RequestLimitException>(
            () => client.SetEndpointMetadata(new EndpointMetadata { EndpointId = "ep-1" }));

        Assert.IsTrue(logger.Entries.Any(e => e.Level == LogLevel.Error && e.Exception is CosmosException),
            "Upsert failures must surface through the Microsoft.Extensions.Logging logger.");
    }

    private sealed class CapturingLogger : ILogger<CosmosDbClient>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private sealed class FakeCosmosClientAdapter : ICosmosClientAdapter, ICosmosDatabaseAdapter
    {
        private readonly FakeContainerAdapter _container;

        public FakeCosmosClientAdapter(FakeContainerAdapter container = null) => _container = container ?? new FakeContainerAdapter();

        public ICosmosDatabaseAdapter GetDatabase(string id) => this;

        public ICosmosContainerAdapter GetContainer(string id) => _container;

        public Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(string id, string partitionKeyPath)
            => Task.FromResult<ICosmosContainerAdapter>(_container);
    }

    private sealed class FakeContainerAdapter : ICosmosContainerAdapter
    {
        public Exception UpsertException { get; set; }

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
            => throw UpsertException ?? new NotSupportedException();

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
