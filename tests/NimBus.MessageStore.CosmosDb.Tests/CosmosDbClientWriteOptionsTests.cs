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
/// Hot-path Cosmos writes (message tracking upserts, message/audit stores) only
/// ever read the response status code, so they must send
/// <c>EnableContentResponseOnWrite = false</c> — otherwise every write echoes the
/// whole document (EventJson included) back over the wire.
/// </summary>
[TestClass]
public sealed class CosmosDbClientWriteOptionsTests
{
    [TestMethod]
    public async Task UploadPendingMessage_suppresses_content_response_on_write()
    {
        var container = new UpsertRecordingContainerAdapter();
        var client = new CosmosDbClient(new SingleContainerClientAdapter(container));

        await client.UploadPendingMessage("event-1", "session-1", "endpoint-1", NewEvent());

        AssertContentSuppressed(container);
    }

    [TestMethod]
    public async Task UploadCompletedMessage_suppresses_content_response_on_write()
    {
        var container = new UpsertRecordingContainerAdapter();
        var client = new CosmosDbClient(new SingleContainerClientAdapter(container));

        await client.UploadCompletedMessage("event-1", "session-1", "endpoint-1", NewEvent());

        AssertContentSuppressed(container);
    }

    [TestMethod]
    public async Task StoreMessage_suppresses_content_response_on_write()
    {
        var container = new UpsertRecordingContainerAdapter();
        var client = new CosmosDbClient(new SingleContainerClientAdapter(container));

        await client.StoreMessage(new MessageEntity
        {
            MessageId = "message-1",
            EventId = "event-1",
            EndpointId = "endpoint-1",
        });

        AssertContentSuppressed(container);
    }

    [TestMethod]
    public async Task StoreMessageAudit_suppresses_content_response_on_write()
    {
        var container = new UpsertRecordingContainerAdapter();
        var client = new CosmosDbClient(new SingleContainerClientAdapter(container));

        await client.StoreMessageAudit("event-1", new MessageAuditEntity(), "endpoint-1", "event-type-1");

        AssertContentSuppressed(container);
    }

    private static void AssertContentSuppressed(UpsertRecordingContainerAdapter container)
    {
        Assert.AreEqual(1, container.CapturedRequestOptions.Count, "Expected exactly one upsert.");
        var options = container.CapturedRequestOptions.Single();
        Assert.IsNotNull(options, "Hot-path upserts must pass ItemRequestOptions.");
        Assert.AreEqual(false, options.EnableContentResponseOnWrite,
            "Hot-path upserts must not echo the written document back in the response.");
    }

    private static UnresolvedEvent NewEvent() => new()
    {
        EventId = "event-1",
        EventTypeId = "event-type-1",
    };

    private sealed class SingleContainerClientAdapter : ICosmosClientAdapter, ICosmosDatabaseAdapter
    {
        private readonly ICosmosContainerAdapter _container;

        public SingleContainerClientAdapter(ICosmosContainerAdapter container) => _container = container;

        public ICosmosDatabaseAdapter GetDatabase(string id) => this;

        public ICosmosContainerAdapter GetContainer(string id) => _container;

        public Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(string id, string partitionKeyPath)
            => Task.FromResult(_container);
    }

    private sealed class UpsertRecordingContainerAdapter : ICosmosContainerAdapter
    {
        public List<ItemRequestOptions> CapturedRequestOptions { get; } = new();

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

        public Task<ItemResponse<T>> UpsertItemAsync<T>(T item, PartitionKey partitionKey = default, ItemRequestOptions requestOptions = null)
        {
            CapturedRequestOptions.Add(requestOptions);
            // The client is constructed without a logger, so the null-conditional
            // trace log never dereferences the (null) response.
            return Task.FromResult<ItemResponse<T>>(null);
        }

        public Task<ItemResponse<T>> CreateItemAsync<T>(T item, PartitionKey partitionKey = default)
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
