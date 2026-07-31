using Microsoft.Azure.Cosmos;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace NimBus.MessageStore;

/// <summary>
/// Cosmos DB implementation of <see cref="IAccessControlStore"/> (spec 026).
/// Carved out of <see cref="CosmosDbClient"/>, which keeps the container cache
/// and exposes the accesscontrol container via the injected accessor.
/// </summary>
internal sealed class CosmosDbAccessControlStore : IAccessControlStore
{
    private readonly Func<Task<ICosmosContainerAdapter>> _getContainer;

    // Writes only ever inspect StatusCode; skipping the response body saves
    // egress and response-deserialization (mirrors CosmosDbClient's hot paths).
    private static readonly ItemRequestOptions SuppressContentOnWrite = new() { EnableContentResponseOnWrite = false };

    public CosmosDbAccessControlStore(Func<Task<ICosmosContainerAdapter>> getContainer)
    {
        _getContainer = getContainer;
    }

    public Task<AccessControlList?> GetSiteAccessControl()
        => ReadAccessControlItem(AccessControlList.SiteId);

    public Task SetSiteAccessControl(AccessControlList accessControl)
    {
        accessControl.Id = AccessControlList.SiteId;
        accessControl.EndpointId = null;
        return UpsertAccessControlItem(accessControl);
    }

    public Task<AccessControlList?> GetEndpointAccessControl(string endpointId)
        => ReadAccessControlItem(AccessControlList.IdForEndpoint(endpointId));

    public async Task<IReadOnlyList<AccessControlList>> GetEndpointAccessControls()
    {
        var container = await _getContainer();
        var results = new List<AccessControlList>();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE STARTSWITH(c.id, @prefix)")
            .WithParameter("@prefix", AccessControlList.EndpointIdPrefix);
        using var iterator = container.GetItemQueryIterator<AccessControlList>(query);
        while (iterator.HasMoreResults)
            results.AddRange(await iterator.ReadNextAsync());
        return results;
    }

    public Task SetEndpointAccessControl(string endpointId, AccessControlList accessControl)
    {
        accessControl.Id = AccessControlList.IdForEndpoint(endpointId);
        accessControl.EndpointId = endpointId;
        return UpsertAccessControlItem(accessControl);
    }

    private async Task<AccessControlList?> ReadAccessControlItem(string id)
    {
        var container = await _getContainer();
        try
        {
            var resp = await container.ReadItemAsync<AccessControlList>(id, new PartitionKey(id));
            return resp.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task UpsertAccessControlItem(AccessControlList accessControl)
    {
        var container = await _getContainer();
        await container.UpsertItemAsync(accessControl, new PartitionKey(accessControl.Id), SuppressContentOnWrite);
    }
}
