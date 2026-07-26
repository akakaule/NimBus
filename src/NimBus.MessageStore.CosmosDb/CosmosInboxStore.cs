using System.Globalization;
using System.Net;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using NimBus.Core.Inbox;

namespace NimBus.MessageStore;

/// <summary>
/// Cosmos DB-backed inbox store using one document per logical message identifier.
/// </summary>
public sealed class CosmosInboxStore : IInboxStore
{
    private readonly object _containerLock = new();
    private readonly ICosmosClientAdapter _cosmosClient;
    private readonly CosmosInboxOptions _options;
    private readonly TimeProvider _timeProvider;
    private Task<ICosmosContainerAdapter>? _containerTask;

    /// <summary>
    /// Initializes a Cosmos inbox using the supplied SDK client.
    /// </summary>
    /// <param name="cosmosClient">The shared Cosmos SDK client.</param>
    /// <param name="options">Optional inbox container settings.</param>
    public CosmosInboxStore(CosmosClient cosmosClient, CosmosInboxOptions? options = null)
        : this(
            new CosmosClientAdapter(cosmosClient ?? throw new ArgumentNullException(nameof(cosmosClient))),
            options,
            TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a Cosmos inbox using an adapter, primarily for advanced integrations and tests.
    /// </summary>
    /// <param name="cosmosClient">The Cosmos client adapter.</param>
    /// <param name="options">Optional inbox container settings.</param>
    public CosmosInboxStore(ICosmosClientAdapter cosmosClient, CosmosInboxOptions? options = null)
        : this(cosmosClient, options, TimeProvider.System)
    {
    }

    internal CosmosInboxStore(
        ICosmosClientAdapter cosmosClient,
        CosmosInboxOptions? options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(cosmosClient);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var configuredOptions = options ?? new CosmosInboxOptions();
        configuredOptions.Validate();
        _options = new CosmosInboxOptions
        {
            DatabaseId = configuredOptions.DatabaseId,
            ContainerId = configuredOptions.ContainerId,
            PurgeBatchSize = configuredOptions.PurgeBatchSize,
            AllowRelaxedConsistency = configuredOptions.AllowRelaxedConsistency,
        };
        _cosmosClient = new TransientTranslatingCosmosClientAdapter(cosmosClient, logger: null);
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<bool> HasProcessedAsync(
        string endpointId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateIdentity(endpointId, messageId);
        var documentId = GetDocumentId(endpointId, messageId);
        var container = await GetContainerAsync(cancellationToken);

        try
        {
            await container.ReadItemAsync<InboxDocument>(
                documentId,
                new PartitionKey(documentId),
                requestOptions: null!,
                cancellationToken);
            return true;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    /// <inheritdoc />
    public async Task RecordProcessedAsync(
        string endpointId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateIdentity(endpointId, messageId);
        var documentId = GetDocumentId(endpointId, messageId);
        var document = new InboxDocument
        {
            Id = documentId,
            EndpointId = endpointId,
            MessageId = messageId,
            CreatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
        };
        var container = await GetContainerAsync(cancellationToken);

        try
        {
            await container.CreateItemAsync(
                document,
                new PartitionKey(documentId),
                cancellationToken);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            // A concurrent first writer won. CreateItem (rather than upsert) keeps
            // the original CreatedAtUtc used for retention decisions.
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <inheritdoc />
    public async Task<int> PurgeExpiredAsync(
        string endpointId,
        DateTimeOffset olderThan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        var container = await GetContainerAsync(cancellationToken);
        // Retention is configured per subscriber, so the purge is scoped to the caller's
        // endpoint — a short-retention subscriber must never delete another endpoint's
        // still-valid records from a shared container.
        var query = new QueryDefinition(
                "SELECT TOP @batchSize c.id, c._etag FROM c WHERE c.endpointId = @endpointId AND c.createdAtUtc < @olderThan ORDER BY c.createdAtUtc")
            .WithParameter("@batchSize", _options.PurgeBatchSize)
            .WithParameter("@endpointId", endpointId)
            .WithParameter("@olderThan", olderThan.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        var requestOptions = new QueryRequestOptions { MaxItemCount = _options.PurgeBatchSize };
        using var iterator = container.GetItemQueryIterator<InboxDocumentId>(
            query,
            continuationToken: null,
            requestOptions);

        if (!iterator.HasMoreResults)
        {
            return 0;
        }

        var page = await iterator.ReadNextAsync(cancellationToken);
        var removed = 0;
        foreach (var document in page)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // The ETag precondition pins the delete to the queried document version: a
                // concurrent worker may delete it first (404), and a redelivery may recreate a
                // fresh record under the same deterministic id (412). Both are benign races —
                // an unconditional delete would destroy that fresh, unexpired record.
                await container.DeleteItemAsync<InboxDocument>(
                    document.Id,
                    new PartitionKey(document.Id),
                    new ItemRequestOptions { IfMatchEtag = document.ETag },
                    cancellationToken);
                removed++;
            }
            catch (CosmosException exception) when (
                exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        return removed;
    }

    internal static string GetDocumentId(string endpointId, string messageId)
    {
        // The shared hash keeps the Cosmos document id and the SQL provider's key derived from
        // one canonical, unambiguous (endpoint, message) encoding.
        return Convert.ToHexString(InboxIdentity.ComputeHash(endpointId, messageId));
    }

    private async Task<ICosmosContainerAdapter> GetContainerAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<ICosmosContainerAdapter> containerTask;
        lock (_containerLock)
        {
            _containerTask ??= CreateContainerAsync(cancellationToken);
            containerTask = _containerTask;
        }

        try
        {
            return await containerTask.WaitAsync(cancellationToken);
        }
        catch
        {
            if (containerTask.IsCanceled || containerTask.IsFaulted)
            {
                lock (_containerLock)
                {
                    if (ReferenceEquals(_containerTask, containerTask))
                    {
                        _containerTask = null;
                    }
                }
            }

            throw;
        }
    }

    private async Task<ICosmosContainerAdapter> CreateContainerAsync(CancellationToken cancellationToken)
    {
        // The validation shares the lazy container task, so it runs once per store and is
        // retried together with container creation after a failure.
        if (!_options.AllowRelaxedConsistency)
        {
            await ValidateStrongConsistencyAsync(cancellationToken);
        }

        var database = _cosmosClient.GetDatabase(_options.DatabaseId);
        return await database.CreateContainerIfNotExistsAsync(
            _options.ContainerId,
            "/id",
            cancellationToken);
    }

    private async Task ValidateStrongConsistencyAsync(CancellationToken cancellationToken)
    {
        // The duplicate check must observe the latest committed record even when another
        // process wrote it and crashed before broker settlement. Cosmos session tokens never
        // cross process boundaries, so only Strong consistency gives that guarantee; anything
        // weaker leaves a cross-replica window where a stale miss re-runs the handler and the
        // recording conflict is silently swallowed.
        ConsistencyLevel? effectiveLevel;
        try
        {
            effectiveLevel = await _cosmosClient.GetAccountConsistencyLevelAsync(cancellationToken);
        }
        catch (NotSupportedException exception)
        {
            throw new InvalidOperationException(
                $"The configured {nameof(ICosmosClientAdapter)} cannot report the account consistency level, " +
                "so the inbox cannot verify that duplicate checks observe the latest committed records. " +
                $"Override {nameof(ICosmosClientAdapter.GetAccountConsistencyLevelAsync)} on the adapter, or set " +
                $"{nameof(CosmosInboxOptions)}.{nameof(CosmosInboxOptions.AllowRelaxedConsistency)} to acknowledge " +
                "the cross-replica duplicate window.",
                exception);
        }

        if (effectiveLevel != ConsistencyLevel.Strong)
        {
            throw new InvalidOperationException(
                $"The Cosmos account's effective consistency level is '{effectiveLevel?.ToString() ?? "unknown"}', " +
                "but the inbox requires 'Strong' so a duplicate check on any process observes records committed by " +
                "a consumer that crashed before broker settlement. Configure the account for Strong consistency, or " +
                $"set {nameof(CosmosInboxOptions)}.{nameof(CosmosInboxOptions.AllowRelaxedConsistency)} to accept " +
                "the residual duplicate-side-effect risk (for example when a single consumer process serves the endpoint).");
        }
    }

    private static void ValidateIdentity(string endpointId, string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
    }
}

internal sealed class InboxDocument
{
    [JsonProperty(PropertyName = "id")]
    public string Id { get; init; } = string.Empty;

    [JsonProperty(PropertyName = "endpointId")]
    public string EndpointId { get; init; } = string.Empty;

    [JsonProperty(PropertyName = "messageId")]
    public string MessageId { get; init; } = string.Empty;

    [JsonProperty(PropertyName = "createdAtUtc")]
    public DateTime CreatedAtUtc { get; init; }
}

internal sealed class InboxDocumentId
{
    [JsonProperty(PropertyName = "id")]
    public string Id { get; init; } = string.Empty;

    [JsonProperty(PropertyName = "_etag")]
    public string ETag { get; init; } = string.Empty;
}
