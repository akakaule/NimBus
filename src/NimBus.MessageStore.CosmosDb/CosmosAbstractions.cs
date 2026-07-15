using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.MessageStore;

public interface ICosmosClientAdapter
{
    ICosmosDatabaseAdapter GetDatabase(string id);
}

public interface ICosmosDatabaseAdapter
{
    ICosmosContainerAdapter GetContainer(string id);
    Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(string id, string partitionKeyPath);
}

public interface ICosmosContainerAdapter
{
    FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition);
    FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition, string continuationToken = null, QueryRequestOptions requestOptions = null);
    FeedIterator<T> GetItemQueryIterator<T>(string queryText);
    FeedIterator<T> GetItemQueryIterator<T>(string queryText, string continuationToken = null, QueryRequestOptions requestOptions = null);
    IOrderedQueryable<T> GetItemLinqQueryable<T>(bool allowSynchronousQueryExecution = false, string continuationToken = null, QueryRequestOptions requestOptions = null);
    Task<ItemResponse<T>> CreateItemAsync<T>(T item, PartitionKey partitionKey = default);
    Task<ItemResponse<T>> UpsertItemAsync<T>(T item, PartitionKey partitionKey = default, ItemRequestOptions requestOptions = null);
    Task<ItemResponse<T>> DeleteItemAsync<T>(string id, PartitionKey partitionKey);
    Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey);
    Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey, ItemRequestOptions requestOptions);
    Task<ItemResponse<T>> PatchItemAsync<T>(string id, PartitionKey partitionKey, IReadOnlyList<PatchOperation> patchOperations);
    Task<ContainerResponse> DeleteContainerAsync();
    Task<FeedResponse<T>> ReadManyItemsAsync<T>(IReadOnlyList<(string id, PartitionKey partitionKey)> items);
}

public sealed class CosmosClientAdapter : ICosmosClientAdapter
{
    private readonly CosmosClient _client;
    private readonly ILogger? _logger;

    public CosmosClientAdapter(CosmosClient client)
        : this(client, null)
    {
    }

    internal CosmosClientAdapter(CosmosClient client, ILogger? logger)
    {
        _client = client;
        _logger = logger;
    }

    public ICosmosDatabaseAdapter GetDatabase(string id) => new CosmosDatabaseAdapter(_client.GetDatabase(id), _logger);
}

public sealed class CosmosDatabaseAdapter : ICosmosDatabaseAdapter
{
    private readonly Database _database;
    private readonly ILogger? _logger;

    public CosmosDatabaseAdapter(Database database)
        : this(database, null)
    {
    }

    internal CosmosDatabaseAdapter(Database database, ILogger? logger)
    {
        _database = database;
        _logger = logger;
    }

    public ICosmosContainerAdapter GetContainer(string id) => new CosmosContainerAdapter(_database.GetContainer(id), _logger);

    public async Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(string id, string partitionKeyPath)
    {
        var response = await CosmosExceptionTranslation.TranslateTransientAsync(
            () => _database.CreateContainerIfNotExistsAsync(id, partitionKeyPath),
            _logger);
        return new CosmosContainerAdapter(response.Container, _logger);
    }
}

public sealed class CosmosContainerAdapter : ICosmosContainerAdapter
{
    private readonly Container _container;
    private readonly ILogger? _logger;

    public CosmosContainerAdapter(Container container)
        : this(container, null)
    {
    }

    internal CosmosContainerAdapter(Container container, ILogger? logger)
    {
        _container = container;
        _logger = logger;
    }

    public FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition) =>
        CosmosExceptionTranslation.Wrap(_container.GetItemQueryIterator<T>(queryDefinition), _logger);

    public FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition, string continuationToken = null, QueryRequestOptions requestOptions = null) =>
        CosmosExceptionTranslation.Wrap(_container.GetItemQueryIterator<T>(queryDefinition, continuationToken, requestOptions), _logger);

    public FeedIterator<T> GetItemQueryIterator<T>(string queryText) =>
        CosmosExceptionTranslation.Wrap(_container.GetItemQueryIterator<T>(queryText), _logger);

    public FeedIterator<T> GetItemQueryIterator<T>(string queryText, string continuationToken = null, QueryRequestOptions requestOptions = null) =>
        CosmosExceptionTranslation.Wrap(_container.GetItemQueryIterator<T>(queryText, continuationToken, requestOptions), _logger);

    public IOrderedQueryable<T> GetItemLinqQueryable<T>(bool allowSynchronousQueryExecution = false, string continuationToken = null, QueryRequestOptions requestOptions = null) =>
        _container.GetItemLinqQueryable<T>(allowSynchronousQueryExecution, continuationToken, requestOptions);

    public Task<ItemResponse<T>> CreateItemAsync<T>(T item, PartitionKey partitionKey = default) =>
        CosmosExceptionTranslation.TranslateTransientAsync(() => _container.CreateItemAsync(item, partitionKey), _logger);

    public Task<ItemResponse<T>> UpsertItemAsync<T>(T item, PartitionKey partitionKey = default, ItemRequestOptions requestOptions = null) =>
        CosmosExceptionTranslation.TranslateTransientAsync(() => _container.UpsertItemAsync(item, partitionKey, requestOptions), _logger);

    public Task<ItemResponse<T>> DeleteItemAsync<T>(string id, PartitionKey partitionKey) =>
        CosmosExceptionTranslation.TranslateTransientAsync(() => _container.DeleteItemAsync<T>(id, partitionKey), _logger);

    public Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey) =>
        CosmosExceptionTranslation.TranslateTransientAsync(() => _container.ReadItemAsync<T>(id, partitionKey), _logger);

    public Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey, ItemRequestOptions requestOptions) =>
        CosmosExceptionTranslation.TranslateTransientAsync(() => _container.ReadItemAsync<T>(id, partitionKey, requestOptions), _logger);

    public Task<ItemResponse<T>> PatchItemAsync<T>(string id, PartitionKey partitionKey, IReadOnlyList<PatchOperation> patchOperations) =>
        CosmosExceptionTranslation.TranslateTransientAsync(() => _container.PatchItemAsync<T>(id, partitionKey, patchOperations), _logger);

    public Task<ContainerResponse> DeleteContainerAsync() =>
        CosmosExceptionTranslation.TranslateTransientAsync(() => _container.DeleteContainerAsync(), _logger);

    public Task<FeedResponse<T>> ReadManyItemsAsync<T>(IReadOnlyList<(string id, PartitionKey partitionKey)> items) =>
        CosmosExceptionTranslation.TranslateTransientAsync(() => _container.ReadManyItemsAsync<T>(items), _logger);
}
