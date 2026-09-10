using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.CosmosDB;

namespace NimBus.MessageStore;

/// <summary>
/// Manages Cosmos DB containers through Azure Resource Manager for Entra-authenticated hosts.
/// </summary>
internal sealed class ArmCosmosContainerAdmin : ICosmosContainerAdmin
{
    private readonly ArmClient _client;
    private readonly ResourceIdentifier _databaseResourceId;

    public ArmCosmosContainerAdmin(ArmClient client, string accountResourceId)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountResourceId);

        _client = client;
        var accountId = new ResourceIdentifier(accountResourceId);
        if (accountId.ResourceType != CosmosDBAccountResource.ResourceType)
            throw new ArgumentException("The configured Cosmos account resource ID is invalid.", nameof(accountResourceId));

        _databaseResourceId = new ResourceIdentifier(
            $"{accountId}/sqlDatabases/{CosmosDbClient.DatabaseId}");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListContainerIdsAsync(CancellationToken cancellationToken = default)
    {
        var database = _client.GetCosmosDBSqlDatabaseResource(_databaseResourceId);
        var result = new List<string>();
        await foreach (var container in database.GetCosmosDBSqlContainers()
            .GetAllAsync(cancellationToken: cancellationToken))
        {
            result.Add(container.Id.Name);
        }

        return result.Order(StringComparer.Ordinal).ToArray();
    }

    /// <inheritdoc />
    public async Task<bool> DeleteContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        if (CosmosContainerDefaults.ReservedContainerIds.Contains(containerId))
            throw new InvalidOperationException("Internal NimBus containers cannot be deleted through container administration.");

        var resourceId = new ResourceIdentifier($"{_databaseResourceId}/containers/{containerId}");
        try
        {
            await _client.GetCosmosDBSqlContainerResource(resourceId)
                .DeleteAsync(WaitUntil.Completed, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return false;
        }
    }
}
