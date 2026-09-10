using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;

namespace NimBus.MessageStore;

/// <summary>Cosmos implementation of container administration.</summary>
public sealed class CosmosContainerAdmin(CosmosClient client) : ICosmosContainerAdmin
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListContainerIdsAsync(CancellationToken cancellationToken = default)
    {
        var iterator = client.GetDatabase(CosmosDbClient.DatabaseId)
            .GetContainerQueryIterator<ContainerProperties>();
        var result = new List<string>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            result.AddRange(page.Select(container => container.Id));
        }

        return result.Order(StringComparer.Ordinal).ToArray();
    }

    /// <inheritdoc />
    public async Task<bool> DeleteContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        if (CosmosContainerDefaults.ReservedContainerIds.Contains(containerId))
            throw new InvalidOperationException("Internal NimBus containers cannot be deleted through container administration.");

        try
        {
            await client.GetDatabase(CosmosDbClient.DatabaseId)
                .GetContainer(containerId)
                .DeleteContainerAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
