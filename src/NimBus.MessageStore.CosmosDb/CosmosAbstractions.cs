using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
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
    Task<ItemResponse<T>> CreateItemAsync<T>(T item, PartitionKey? partitionKey = null, ItemRequestOptions requestOptions = null);
    Task<ItemResponse<T>> UpsertItemAsync<T>(T item, PartitionKey partitionKey = default);
    Task<ItemResponse<T>> UpsertItemAsync<T>(T item, PartitionKey partitionKey, ItemRequestOptions requestOptions);
    Task<ItemResponse<T>> ReplaceItemAsync<T>(T item, string id, PartitionKey partitionKey, ItemRequestOptions requestOptions = null);
    Task<ItemResponse<T>> DeleteItemAsync<T>(string id, PartitionKey partitionKey);
    Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey);
    Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey, ItemRequestOptions requestOptions);
    Task<ItemResponse<T>> PatchItemAsync<T>(string id, PartitionKey partitionKey, IReadOnlyList<PatchOperation> patchOperations);
    Task<ItemResponse<T>> PatchItemAsync<T>(string id, PartitionKey partitionKey, IReadOnlyList<PatchOperation> patchOperations, PatchItemRequestOptions requestOptions);
    Task<ContainerResponse> DeleteContainerAsync();
    Task<FeedResponse<T>> ReadManyItemsAsync<T>(IReadOnlyList<(string id, PartitionKey partitionKey)> items);
}

public sealed class CosmosClientAdapter : ICosmosClientAdapter
{
    private readonly CosmosClient _client;

    public CosmosClientAdapter(CosmosClient client)
    {
        _client = client;
    }

    public ICosmosDatabaseAdapter GetDatabase(string id) => new CosmosDatabaseAdapter(_client.GetDatabase(id));
}

public sealed class CosmosDatabaseAdapter : ICosmosDatabaseAdapter
{
    private readonly Database _database;

    public CosmosDatabaseAdapter(Database database)
    {
        _database = database;
    }

    public ICosmosContainerAdapter GetContainer(string id) => new CosmosContainerAdapter(_database.GetContainer(id));

    public async Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(string id, string partitionKeyPath)
    {
        var response = await _database.CreateContainerIfNotExistsAsync(id, partitionKeyPath);
        return new CosmosContainerAdapter(response.Container);
    }
}

public sealed class CosmosContainerAdapter : ICosmosContainerAdapter
{
    private readonly Container _container;

    public CosmosContainerAdapter(Container container)
    {
        _container = container;
    }

    public FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition) =>
        _container.GetItemQueryIterator<T>(queryDefinition);

    public FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition, string continuationToken = null, QueryRequestOptions requestOptions = null) =>
        _container.GetItemQueryIterator<T>(queryDefinition, continuationToken, requestOptions);

    public FeedIterator<T> GetItemQueryIterator<T>(string queryText) =>
        _container.GetItemQueryIterator<T>(queryText);

    public FeedIterator<T> GetItemQueryIterator<T>(string queryText, string continuationToken = null, QueryRequestOptions requestOptions = null) =>
        _container.GetItemQueryIterator<T>(queryText, continuationToken, requestOptions);

    public IOrderedQueryable<T> GetItemLinqQueryable<T>(bool allowSynchronousQueryExecution = false, string continuationToken = null, QueryRequestOptions requestOptions = null) =>
        _container.GetItemLinqQueryable<T>(allowSynchronousQueryExecution, continuationToken, requestOptions);

    public Task<ItemResponse<T>> CreateItemAsync<T>(T item, PartitionKey? partitionKey = null, ItemRequestOptions requestOptions = null) =>
        _container.CreateItemAsync(item, partitionKey, requestOptions);

    public Task<ItemResponse<T>> UpsertItemAsync<T>(T item, PartitionKey partitionKey = default) =>
        _container.UpsertItemAsync(item, partitionKey);

    public Task<ItemResponse<T>> UpsertItemAsync<T>(T item, PartitionKey partitionKey, ItemRequestOptions requestOptions) =>
        _container.UpsertItemAsync(item, partitionKey, requestOptions);

    public Task<ItemResponse<T>> ReplaceItemAsync<T>(T item, string id, PartitionKey partitionKey, ItemRequestOptions requestOptions = null) =>
        _container.ReplaceItemAsync(item, id, partitionKey, requestOptions);

    public Task<ItemResponse<T>> DeleteItemAsync<T>(string id, PartitionKey partitionKey) =>
        _container.DeleteItemAsync<T>(id, partitionKey);

    public Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey) =>
        _container.ReadItemAsync<T>(id, partitionKey);

    public Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey, ItemRequestOptions requestOptions) =>
        _container.ReadItemAsync<T>(id, partitionKey, requestOptions);

    public Task<ItemResponse<T>> PatchItemAsync<T>(string id, PartitionKey partitionKey, IReadOnlyList<PatchOperation> patchOperations) =>
        _container.PatchItemAsync<T>(id, partitionKey, patchOperations);

    public Task<ItemResponse<T>> PatchItemAsync<T>(string id, PartitionKey partitionKey, IReadOnlyList<PatchOperation> patchOperations, PatchItemRequestOptions requestOptions) =>
        _container.PatchItemAsync<T>(id, partitionKey, patchOperations, requestOptions);

    public Task<ContainerResponse> DeleteContainerAsync() =>
        _container.DeleteContainerAsync();

    public Task<FeedResponse<T>> ReadManyItemsAsync<T>(IReadOnlyList<(string id, PartitionKey partitionKey)> items) =>
        _container.ReadManyItemsAsync<T>(items);
}
