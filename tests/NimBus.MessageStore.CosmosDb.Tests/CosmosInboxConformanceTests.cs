#pragma warning disable CA1707, CA2007
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Inbox;
using NimBus.Testing.Conformance;

namespace NimBus.MessageStore.CosmosDb.Tests;

[TestClass]
public sealed class FakeCosmosInboxConformanceTests : InboxStoreConformanceTests
{
    private ManualTimeProvider _timeProvider = null!;

    protected override Task<IInboxStore> CreateStoreAsync()
    {
        _timeProvider = new ManualTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return Task.FromResult<IInboxStore>(new CosmosInboxStore(
            new ConformanceCosmosInfrastructure(),
            new CosmosInboxOptions(),
            _timeProvider));
    }

    protected override Task<DateTimeOffset> AdvancePastFirstRecordAsync()
    {
        _timeProvider.Advance(TimeSpan.FromMinutes(1));
        return Task.FromResult(_timeProvider.GetUtcNow());
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }
}

[TestClass]
[DoNotParallelize]
public sealed class LiveCosmosInboxConformanceTests : InboxStoreConformanceTests
{
    protected override Task<IInboxStore> CreateStoreAsync()
    {
        // No cross-run drain is needed: every conformance test instance scopes its endpoint
        // ids with a fresh GUID, and both purge counting and record checks are now
        // endpoint-scoped, so leftover documents from earlier runs cannot skew assertions.
        return Task.FromResult(CosmosDbStoreTestHarness.CreateInboxStore());
    }
}

internal sealed class ConformanceCosmosInfrastructure : ICosmosClientAdapter, ICosmosDatabaseAdapter
{
    private readonly ConformanceCosmosContainer _container = new();

    public Task<ConsistencyLevel?> GetAccountConsistencyLevelAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ConsistencyLevel?>(ConsistencyLevel.Strong);
    }

    public ICosmosDatabaseAdapter GetDatabase(string id) => this;

    public ICosmosContainerAdapter GetContainer(string id) => _container;

    public Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(string id, string partitionKeyPath)
        => Task.FromResult<ICosmosContainerAdapter>(_container);

    public Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(
        string id,
        string partitionKeyPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ICosmosContainerAdapter>(_container);
    }
}

internal sealed class ConformanceCosmosContainer : ICosmosContainerAdapter
{
    private readonly ConcurrentDictionary<string, (InboxDocument Document, string ETag)> _documents = new(StringComparer.Ordinal);

    public FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition)
        => GetItemQueryIterator<T>(queryDefinition, null, null);

    public FeedIterator<T> GetItemQueryIterator<T>(
        QueryDefinition queryDefinition,
        string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
    {
        var parameters = queryDefinition.GetQueryParameters();
        var cutoffValue = (string)parameters.Single(parameter => parameter.Name == "@olderThan").Value;
        var batchSize = (int)parameters.Single(parameter => parameter.Name == "@batchSize").Value;
        var endpointId = (string)parameters.Single(parameter => parameter.Name == "@endpointId").Value;
        var cutoff = DateTime.Parse(
            cutoffValue,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        var ids = _documents.Values
            .Where(entry => string.Equals(entry.Document.EndpointId, endpointId, StringComparison.Ordinal)
                && entry.Document.CreatedAtUtc < cutoff)
            .OrderBy(entry => entry.Document.CreatedAtUtc)
            .Take(batchSize)
            .Select(entry => (T)(object)new InboxDocumentId { Id = entry.Document.Id, ETag = entry.ETag })
            .ToArray();
        return new ConformanceFeedIterator<T>(ids);
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
        if (!_documents.TryAdd(document.Id, (document, Guid.NewGuid().ToString("N"))))
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
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_documents.TryRemove(id, out _))
        {
            throw NewCosmosException(HttpStatusCode.NotFound);
        }

        return Task.FromResult<ItemResponse<T>>(null!);
    }

    public Task<ItemResponse<T>> DeleteItemAsync<T>(
        string id,
        PartitionKey partitionKey,
        ItemRequestOptions requestOptions,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_documents.TryGetValue(id, out var entry))
        {
            throw NewCosmosException(HttpStatusCode.NotFound);
        }

        if (requestOptions?.IfMatchEtag is { } etag && !string.Equals(etag, entry.ETag, StringComparison.Ordinal))
        {
            throw NewCosmosException(HttpStatusCode.PreconditionFailed);
        }

        // Value-conditioned removal keeps the compare-and-delete atomic under concurrency.
        if (!_documents.TryRemove(new KeyValuePair<string, (InboxDocument, string)>(id, entry)))
        {
            throw NewCosmosException(HttpStatusCode.PreconditionFailed);
        }

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
        if (!_documents.ContainsKey(id))
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

internal sealed class ConformanceFeedIterator<T>(IReadOnlyList<T> items) : FeedIterator<T>
{
    private bool _consumed;

    public override bool HasMoreResults => !_consumed;

    public override Task<FeedResponse<T>> ReadNextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _consumed = true;
        return Task.FromResult<FeedResponse<T>>(new ConformanceFeedResponse<T>(items));
    }
}

internal sealed class ConformanceFeedResponse<T>(IReadOnlyList<T> items) : FeedResponse<T>
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
