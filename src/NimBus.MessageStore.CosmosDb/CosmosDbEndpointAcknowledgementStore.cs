using Microsoft.Azure.Cosmos;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.MessageStore;

/// <summary>
/// Cosmos DB implementation of <see cref="IEndpointAcknowledgementStore"/>: one document per
/// endpoint (id = endpoint id) in the <c>endpointacknowledgements</c> container, created on
/// first use by <see cref="CosmosDbClient"/>.
/// </summary>
internal sealed class CosmosDbEndpointAcknowledgementStore : IEndpointAcknowledgementStore
{
    private readonly Func<Task<ICosmosContainerAdapter>> _getContainer;

    private static readonly ItemRequestOptions SuppressContentOnWrite = new() { EnableContentResponseOnWrite = false };

    public CosmosDbEndpointAcknowledgementStore(Func<Task<ICosmosContainerAdapter>> getContainer)
    {
        _getContainer = getContainer;
    }

    public async Task<IReadOnlyList<EndpointAcknowledgement>> GetEndpointAcknowledgements()
    {
        var container = await _getContainer();
        var results = new List<EndpointAcknowledgement>();
        using var iterator = container.GetItemQueryIterator<EndpointAcknowledgement>(new QueryDefinition("SELECT * FROM c"));
        while (iterator.HasMoreResults)
        {
            foreach (var acknowledgement in await iterator.ReadNextAsync())
            {
                acknowledgement.AcknowledgedAtUtc = AsUtc(acknowledgement.AcknowledgedAtUtc);
                acknowledgement.ExpiresAtUtc = AsUtc(acknowledgement.ExpiresAtUtc);
                results.Add(acknowledgement);
            }
        }

        return results;
    }

    public async Task SetEndpointAcknowledgement(EndpointAcknowledgement acknowledgement)
    {
        if (string.IsNullOrWhiteSpace(acknowledgement.EndpointId)) throw new ArgumentException("EndpointId is required.", nameof(acknowledgement));

        var container = await _getContainer();
        await container.UpsertItemAsync(acknowledgement, new PartitionKey(acknowledgement.EndpointId), SuppressContentOnWrite);
    }

    public async Task<bool> RemoveEndpointAcknowledgement(string endpointId, string? expectedAcknowledgementId = null)
    {
        var container = await _getContainer();
        var partitionKey = new PartitionKey(endpointId);
        try
        {
            if (expectedAcknowledgementId is null)
            {
                await container.DeleteItemAsync<EndpointAcknowledgement>(endpointId, partitionKey);
                return true;
            }

            // Pin the delete to the version whose token matched: a concurrent re-acknowledge
            // replaces the document, changes its ETag, and the delete fails with 412.
            var current = await container.ReadItemAsync<EndpointAcknowledgement>(endpointId, partitionKey);
            if (current.Resource.AcknowledgementId != expectedAcknowledgementId)
            {
                return false;
            }

            await container.DeleteItemAsync<EndpointAcknowledgement>(
                endpointId,
                partitionKey,
                new ItemRequestOptions { IfMatchEtag = current.ETag },
                CancellationToken.None);
            return true;
        }
        catch (CosmosException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };
}
