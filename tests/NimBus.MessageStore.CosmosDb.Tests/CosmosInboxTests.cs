#pragma warning disable CA1707, CA2007
using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Inbox;

namespace NimBus.MessageStore.CosmosDb.Tests;

[TestClass]
public sealed class CosmosInboxTests
{
    [TestMethod]
    public async Task Record_and_point_read_use_a_deterministic_safe_id_and_preserve_the_first_timestamp()
    {
        var infrastructure = new FakeCosmosInfrastructure();
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new CosmosInboxStore(infrastructure, new CosmosInboxOptions(), timeProvider);
        const string endpointId = "billing";
        const string logicalMessageId = "orders/one?#\\customer";

        await store.RecordProcessedAsync(endpointId, logicalMessageId);
        var expectedPhysicalId = CosmosInboxStore.GetDocumentId(endpointId, logicalMessageId);
        var firstDocument = infrastructure.Container.Documents[expectedPhysicalId];

        timeProvider.Advance(TimeSpan.FromHours(1));
        await store.RecordProcessedAsync(endpointId, logicalMessageId);

        Assert.AreEqual(firstDocument.CreatedAtUtc, infrastructure.Container.Documents[expectedPhysicalId].CreatedAtUtc);
        Assert.AreEqual(endpointId, firstDocument.EndpointId);
        Assert.AreEqual(logicalMessageId, firstDocument.MessageId);
        Assert.IsTrue(Regex.IsMatch(expectedPhysicalId, "^[0-9A-F]{64}$", RegexOptions.CultureInvariant));
        Assert.AreEqual(expectedPhysicalId, CosmosInboxStore.GetDocumentId(endpointId, logicalMessageId));
        Assert.AreNotEqual(expectedPhysicalId, CosmosInboxStore.GetDocumentId("shipping", logicalMessageId));
        Assert.IsTrue(await store.HasProcessedAsync(endpointId, logicalMessageId));
        Assert.IsFalse(await store.HasProcessedAsync("shipping", logicalMessageId));
        Assert.IsFalse(await store.HasProcessedAsync(endpointId, "missing"));
        CollectionAssert.Contains(infrastructure.Container.ReadIds, expectedPhysicalId);
        Assert.AreEqual(new PartitionKey(expectedPhysicalId), infrastructure.Container.ReadPartitionKeys[0]);
        Assert.AreEqual("MessageDatabase", infrastructure.DatabaseId);
        Assert.AreEqual("inbox", infrastructure.ContainerId);
        Assert.AreEqual("/id", infrastructure.PartitionKeyPath);
    }

    [TestMethod]
    public async Task PurgeExpiredAsync_reads_one_bounded_page_and_ignores_delete_races()
    {
        var infrastructure = new FakeCosmosInfrastructure();
        infrastructure.Container.PurgeResults.AddRange(
        [
            new InboxDocumentId { Id = "id-1", ETag = "etag-1" },
            new InboxDocumentId { Id = "id-2", ETag = "etag-2" },
            new InboxDocumentId { Id = "id-3", ETag = "etag-3" },
        ]);
        infrastructure.Container.MissingOnDelete.Add("id-2");
        var store = new CosmosInboxStore(infrastructure, new CosmosInboxOptions { PurgeBatchSize = 3 });
        using var cancellation = new CancellationTokenSource();

        var deleted = await store.PurgeExpiredAsync(DateTimeOffset.UtcNow, cancellation.Token);

        Assert.AreEqual(2, deleted);
        Assert.AreEqual(1, infrastructure.Container.QueryReadCount);
        Assert.AreEqual(3, infrastructure.Container.QueryMaxItemCount);
        Assert.AreEqual(cancellation.Token, infrastructure.Container.QueryReadToken);
        string[] expectedDeletedIds = ["id-1", "id-3"];
        CollectionAssert.AreEquivalent(expectedDeletedIds, infrastructure.Container.DeletedIds);
        Assert.IsTrue(infrastructure.Container.LastQueryText.Contains("TOP @batchSize", StringComparison.Ordinal));
        Assert.IsTrue(infrastructure.Container.LastQueryText.Contains("c._etag", StringComparison.Ordinal));
        Assert.AreEqual("etag-1", infrastructure.Container.DeleteRequestEtags["id-1"]);
        Assert.AreEqual("etag-3", infrastructure.Container.DeleteRequestEtags["id-3"]);
    }

    [TestMethod]
    public async Task PurgeExpiredAsync_precondition_failure_spares_a_recreated_record()
    {
        // A stale query page can reference a document another worker already purged and a
        // redelivery recreated under the same deterministic id. The ETag-conditioned delete
        // gets 412 for that fresh record and must leave it in place as a benign race.
        var infrastructure = new FakeCosmosInfrastructure();
        infrastructure.Container.PurgeResults.Add(new InboxDocumentId { Id = "id-1", ETag = "stale-etag" });
        infrastructure.Container.CurrentEtags["id-1"] = "fresh-etag";
        var store = new CosmosInboxStore(infrastructure, new CosmosInboxOptions { PurgeBatchSize = 3 });

        var deleted = await store.PurgeExpiredAsync(DateTimeOffset.UtcNow);

        Assert.AreEqual(0, deleted);
        Assert.AreEqual(0, infrastructure.Container.DeletedIds.Count);
        Assert.AreEqual("stale-etag", infrastructure.Container.DeleteRequestEtags["id-1"]);
    }

    [TestMethod]
    public async Task Purge_with_a_legacy_adapter_fails_closed_instead_of_deleting_unconditionally()
    {
        // An adapter compiled against the pre-options delete surface cannot honor the ETag
        // precondition. Silently dropping the options would let a stale purge page delete a
        // freshly recreated record, so the interface default must throw instead of degrading
        // to the unconditional delete.
        var infrastructure = new LegacyCosmosInfrastructure();
        infrastructure.PurgeResults.Add(new InboxDocumentId { Id = "id-1", ETag = "stale-etag" });
        var store = new CosmosInboxStore(infrastructure, new CosmosInboxOptions { PurgeBatchSize = 3 });

        await Assert.ThrowsExactlyAsync<NotSupportedException>(
            () => store.PurgeExpiredAsync(DateTimeOffset.UtcNow));

        Assert.AreEqual(
            0,
            infrastructure.UnconditionalDeleteCalls,
            "The legacy unconditional delete must never run for a precondition-carrying purge delete.");
    }

    [TestMethod]
    public async Task Operations_stop_before_container_access_when_pre_cancelled()
    {
        var infrastructure = new FakeCosmosInfrastructure();
        var store = new CosmosInboxStore(infrastructure);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => store.RecordProcessedAsync("billing", "cancelled-record", cancellation.Token));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => store.HasProcessedAsync("billing", "cancelled-read", cancellation.Token));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => store.PurgeExpiredAsync(DateTimeOffset.MaxValue, cancellation.Token));

        Assert.AreEqual(0, infrastructure.CreateContainerCalls);
        Assert.AreEqual(0, infrastructure.Container.Documents.Count);
    }

    [TestMethod]
    public async Task Adapter_read_with_literal_default_request_options_remains_source_compatible()
    {
        var infrastructure = new FakeCosmosInfrastructure();

        var exception = await Assert.ThrowsExactlyAsync<CosmosException>(() =>
            infrastructure.Container.ReadItemAsync<InboxDocument>("missing", default, default));

        Assert.AreEqual(HttpStatusCode.NotFound, exception.StatusCode);
    }

    [TestMethod]
    public async Task Constructor_snapshots_validated_options_before_lazy_container_access()
    {
        var infrastructure = new FakeCosmosInfrastructure();
        var options = new CosmosInboxOptions();
        var store = new CosmosInboxStore(infrastructure, options);
        options.DatabaseId = "changed-database";
        options.ContainerId = "changed-container";
        options.PurgeBatchSize = 1_000;

        await store.RecordProcessedAsync("billing", "snapshot-message");
        await store.PurgeExpiredAsync(DateTimeOffset.MinValue);

        Assert.AreEqual("MessageDatabase", infrastructure.DatabaseId);
        Assert.AreEqual("inbox", infrastructure.ContainerId);
        Assert.AreEqual(100, infrastructure.Container.QueryMaxItemCount);
    }

    [TestMethod]
    public void AddNimBusCosmosInbox_registers_the_same_keyed_and_unkeyed_singleton()
    {
        var services = new ServiceCollection();
        var key = Convert.ToBase64String(new byte[64]);
        var client = new CosmosClient("https://localhost:8081", key);

        services.AddNimBusCosmosInbox(client);

        using var provider = services.BuildServiceProvider();
        var unkeyed = provider.GetRequiredService<IInboxStore>();
        var keyed = provider.GetRequiredKeyedService<IInboxStore>(InboxStore.Cosmos);

        Assert.AreSame(unkeyed, keyed);
        Assert.IsInstanceOfType<CosmosInboxStore>(unkeyed);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1_001)]
    public void Constructor_rejects_out_of_range_purge_batch_sizes(int batchSize)
    {
        var options = new CosmosInboxOptions { PurgeBatchSize = batchSize };

        var exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new CosmosInboxStore(new FakeCosmosInfrastructure(), options));

        Assert.AreEqual("PurgeBatchSize", exception.ParamName);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }

    private sealed class FakeCosmosInfrastructure : ICosmosClientAdapter, ICosmosDatabaseAdapter
    {
        public FakeInboxContainer Container { get; } = new();

        public string? DatabaseId { get; private set; }

        public string? ContainerId { get; private set; }

        public string? PartitionKeyPath { get; private set; }

        public int CreateContainerCalls { get; private set; }

        public ICosmosDatabaseAdapter GetDatabase(string id)
        {
            DatabaseId = id;
            return this;
        }

        public ICosmosContainerAdapter GetContainer(string id)
        {
            ContainerId = id;
            return Container;
        }

        public Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(string id, string partitionKeyPath)
            => CreateContainerIfNotExistsAsync(id, partitionKeyPath, CancellationToken.None);

        public Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(
            string id,
            string partitionKeyPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ContainerId = id;
            PartitionKeyPath = partitionKeyPath;
            CreateContainerCalls++;
            return Task.FromResult<ICosmosContainerAdapter>(Container);
        }
    }

    private sealed class FakeInboxContainer : ICosmosContainerAdapter
    {
        public ConcurrentDictionary<string, InboxDocument> Documents { get; } = new(StringComparer.Ordinal);

        public List<string> ReadIds { get; } = [];

        public List<PartitionKey> ReadPartitionKeys { get; } = [];

        public List<InboxDocumentId> PurgeResults { get; } = [];

        public HashSet<string> MissingOnDelete { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> CurrentEtags { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string?> DeleteRequestEtags { get; } = new(StringComparer.Ordinal);

        public List<string> DeletedIds { get; } = [];

        public int QueryReadCount { get; set; }

        public int? QueryMaxItemCount { get; private set; }

        public CancellationToken QueryReadToken { get; set; }

        public string LastQueryText { get; private set; } = string.Empty;

        public FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition)
            => GetItemQueryIterator<T>(queryDefinition, null, null);

        public FeedIterator<T> GetItemQueryIterator<T>(
            QueryDefinition queryDefinition,
            string? continuationToken = null,
            QueryRequestOptions? requestOptions = null)
        {
            LastQueryText = queryDefinition.QueryText;
            QueryMaxItemCount = requestOptions?.MaxItemCount;
            var items = PurgeResults.Select(result => (T)(object)result).ToArray();
            return new TwoPageFeedIterator<T>(items, this);
        }

        public FeedIterator<T> GetItemQueryIterator<T>(string queryText)
            => throw new NotSupportedException();

        public FeedIterator<T> GetItemQueryIterator<T>(
            string queryText,
            string? continuationToken = null,
            QueryRequestOptions? requestOptions = null)
            => throw new NotSupportedException();

        public IOrderedQueryable<T> GetItemLinqQueryable<T>(
            bool allowSynchronousQueryExecution = false,
            string? continuationToken = null,
            QueryRequestOptions? requestOptions = null)
            => throw new NotSupportedException();

        public Task<ItemResponse<T>> CreateItemAsync<T>(T item, PartitionKey partitionKey = default)
            => CreateItemAsync(item, partitionKey, CancellationToken.None);

        public Task<ItemResponse<T>> CreateItemAsync<T>(
            T item,
            PartitionKey partitionKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = (InboxDocument)(object)item!;
            if (!Documents.TryAdd(document.Id, document))
            {
                throw NewCosmosException(HttpStatusCode.Conflict);
            }

            return Task.FromResult<ItemResponse<T>>(null!);
        }

        public Task<ItemResponse<T>> UpsertItemAsync<T>(
            T item,
            PartitionKey partitionKey = default,
            ItemRequestOptions? requestOptions = null)
            => throw new NotSupportedException();

        public Task<ItemResponse<T>> DeleteItemAsync<T>(string id, PartitionKey partitionKey)
            => DeleteItemAsync<T>(id, partitionKey, CancellationToken.None);

        public Task<ItemResponse<T>> DeleteItemAsync<T>(
            string id,
            PartitionKey partitionKey,
            CancellationToken cancellationToken)
            => DeleteItemAsync<T>(id, partitionKey, requestOptions: null!, cancellationToken);

        public Task<ItemResponse<T>> DeleteItemAsync<T>(
            string id,
            PartitionKey partitionKey,
            ItemRequestOptions? requestOptions,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteRequestEtags[id] = requestOptions?.IfMatchEtag;
            if (MissingOnDelete.Contains(id))
            {
                throw NewCosmosException(HttpStatusCode.NotFound);
            }

            if (requestOptions?.IfMatchEtag is { } etag
                && CurrentEtags.TryGetValue(id, out var currentEtag)
                && !string.Equals(etag, currentEtag, StringComparison.Ordinal))
            {
                throw NewCosmosException(HttpStatusCode.PreconditionFailed);
            }

            DeletedIds.Add(id);
            return Task.FromResult<ItemResponse<T>>(null!);
        }

        public Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey)
            => ReadItemAsync<T>(id, partitionKey, requestOptions: null!, CancellationToken.None);

        public Task<ItemResponse<T>> ReadItemAsync<T>(
            string id,
            PartitionKey partitionKey,
            ItemRequestOptions? requestOptions,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadIds.Add(id);
            ReadPartitionKeys.Add(partitionKey);
            if (!Documents.ContainsKey(id))
            {
                throw NewCosmosException(HttpStatusCode.NotFound);
            }

            return Task.FromResult<ItemResponse<T>>(null!);
        }

        public Task<ItemResponse<T>> ReadItemAsync<T>(
            string id,
            PartitionKey partitionKey,
            ItemRequestOptions? requestOptions)
            => ReadItemAsync<T>(id, partitionKey, requestOptions, CancellationToken.None);

        public Task<ItemResponse<T>> PatchItemAsync<T>(
            string id,
            PartitionKey partitionKey,
            IReadOnlyList<PatchOperation> patchOperations)
            => throw new NotSupportedException();

        public Task<ContainerResponse> DeleteContainerAsync()
            => throw new NotSupportedException();

        public Task<FeedResponse<T>> ReadManyItemsAsync<T>(
            IReadOnlyList<(string id, PartitionKey partitionKey)> items)
            => throw new NotSupportedException();

        private static CosmosException NewCosmosException(HttpStatusCode statusCode)
            => new("Simulated Cosmos response.", statusCode, 0, "test-activity", 0);
    }

    /// <summary>
    /// Simulates a third-party adapter compiled against the original adapter surface: it
    /// implements only the abstract interface members, so every options- and
    /// cancellation-aware overload falls back to the interface defaults.
    /// </summary>
    private sealed class LegacyCosmosInfrastructure : ICosmosClientAdapter, ICosmosDatabaseAdapter, ICosmosContainerAdapter
    {
        public List<InboxDocumentId> PurgeResults { get; } = [];

        public int UnconditionalDeleteCalls { get; private set; }

        public ICosmosDatabaseAdapter GetDatabase(string id) => this;

        public ICosmosContainerAdapter GetContainer(string id) => this;

        public Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(string id, string partitionKeyPath)
            => Task.FromResult<ICosmosContainerAdapter>(this);

        public FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition)
            => GetItemQueryIterator<T>(queryDefinition, null, null);

        public FeedIterator<T> GetItemQueryIterator<T>(
            QueryDefinition queryDefinition,
            string? continuationToken = null,
            QueryRequestOptions? requestOptions = null)
            => new SinglePageFeedIterator<T>(PurgeResults.Select(result => (T)(object)result).ToArray());

        public FeedIterator<T> GetItemQueryIterator<T>(string queryText)
            => throw new NotSupportedException();

        public FeedIterator<T> GetItemQueryIterator<T>(
            string queryText,
            string? continuationToken = null,
            QueryRequestOptions? requestOptions = null)
            => throw new NotSupportedException();

        public IOrderedQueryable<T> GetItemLinqQueryable<T>(
            bool allowSynchronousQueryExecution = false,
            string? continuationToken = null,
            QueryRequestOptions? requestOptions = null)
            => throw new NotSupportedException();

        public Task<ItemResponse<T>> CreateItemAsync<T>(T item, PartitionKey partitionKey = default)
            => Task.FromResult<ItemResponse<T>>(null!);

        public Task<ItemResponse<T>> UpsertItemAsync<T>(
            T item,
            PartitionKey partitionKey = default,
            ItemRequestOptions? requestOptions = null)
            => throw new NotSupportedException();

        public Task<ItemResponse<T>> DeleteItemAsync<T>(string id, PartitionKey partitionKey)
        {
            UnconditionalDeleteCalls++;
            return Task.FromResult<ItemResponse<T>>(null!);
        }

        public Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey)
            => throw new CosmosException("Simulated Cosmos response.", HttpStatusCode.NotFound, 0, "test-activity", 0);

        public Task<ItemResponse<T>> ReadItemAsync<T>(
            string id,
            PartitionKey partitionKey,
            ItemRequestOptions? requestOptions)
            => ReadItemAsync<T>(id, partitionKey);

        public Task<ItemResponse<T>> PatchItemAsync<T>(
            string id,
            PartitionKey partitionKey,
            IReadOnlyList<PatchOperation> patchOperations)
            => throw new NotSupportedException();

        public Task<ContainerResponse> DeleteContainerAsync()
            => throw new NotSupportedException();

        public Task<FeedResponse<T>> ReadManyItemsAsync<T>(
            IReadOnlyList<(string id, PartitionKey partitionKey)> items)
            => throw new NotSupportedException();
    }

    private sealed class SinglePageFeedIterator<T>(IReadOnlyList<T> items) : FeedIterator<T>
    {
        public override bool HasMoreResults => true;

        public override Task<FeedResponse<T>> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<FeedResponse<T>>(new ListFeedResponse<T>(items));
        }
    }

    private sealed class TwoPageFeedIterator<T>(IReadOnlyList<T> items, FakeInboxContainer owner) : FeedIterator<T>
    {
        public override bool HasMoreResults => true;

        public override Task<FeedResponse<T>> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.QueryReadCount++;
            owner.QueryReadToken = cancellationToken;
            return Task.FromResult<FeedResponse<T>>(new ListFeedResponse<T>(items));
        }
    }

    private sealed class ListFeedResponse<T>(IReadOnlyList<T> items) : FeedResponse<T>
    {
        public override string ContinuationToken => null!;

        public override int Count => items.Count;

        public override string IndexMetrics => null!;

        public override Headers Headers { get; } = new();

        public override IEnumerable<T> Resource => items;

        public override HttpStatusCode StatusCode => HttpStatusCode.OK;

        public override CosmosDiagnostics Diagnostics => null!;

        public override IEnumerator<T> GetEnumerator() => items.GetEnumerator();
    }
}
