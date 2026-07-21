using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
        DateTimeOffset olderThan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var container = await GetContainerAsync(cancellationToken);
        var query = new QueryDefinition(
                "SELECT TOP @batchSize c.id, c._etag FROM c WHERE c.createdAtUtc < @olderThan ORDER BY c.createdAtUtc")
            .WithParameter("@batchSize", _options.PurgeBatchSize)
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
        // The delimited length prefix makes the concatenation unambiguous for any
        // endpoint/message content, so distinct (endpoint, message) pairs never collide.
        var identity = string.Create(
            CultureInfo.InvariantCulture,
            $"{endpointId.Length}{endpointId}{messageId}");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(bytes);
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

    private Task<ICosmosContainerAdapter> CreateContainerAsync(CancellationToken cancellationToken)
    {
        var database = _cosmosClient.GetDatabase(_options.DatabaseId);
        return database.CreateContainerIfNotExistsAsync(
            _options.ContainerId,
            "/id",
            cancellationToken);
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
