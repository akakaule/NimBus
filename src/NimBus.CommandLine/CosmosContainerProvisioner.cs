using System.Globalization;
using NimBus.Core;
using NimBus.MessageStore;

namespace NimBus.CommandLine;

/// <summary>A planned per-endpoint Cosmos SQL container (control-plane provisioning input).</summary>
internal sealed record EndpointContainerSpec(string ContainerId, string PartitionKeyPath, int DefaultTimeToLive);

/// <summary>
/// Provisions one Cosmos SQL container per catalog endpoint through the Azure CLI control plane
/// as part of <c>nb topology apply</c> / <c>nb setup</c>. The runtime's lazy
/// <c>CreateContainerIfNotExistsAsync</c> only works with account keys — Entra data-plane RBAC
/// (which the deployed apps use) allows item reads/writes but NOT container management — so the
/// first endpoint whose container is missing would fail under managed identity. Shared containers
/// are declared in <c>templates/cosmosDB.bicep</c>; the per-endpoint set is catalog-dependent and
/// therefore provisioned here, next to the Service Bus topology for the same catalog.
/// </summary>
internal sealed class CosmosContainerProvisioner
{
    /// <summary>Database name used by the Cosmos message store (see CosmosDbClient.DatabaseId).</summary>
    internal const string DatabaseName = "MessageDatabase";

    private readonly IAzureCliRunner _az;
    private readonly Func<IPlatform> _platformFactory;

    public CosmosContainerProvisioner(IAzureCliRunner az, Func<IPlatform>? platformFactory = null)
    {
        _az = az;
        _platformFactory = platformFactory ?? ServiceBusTopologyProvisioner.DefaultPlatformFactory;
    }

    /// <summary>
    /// Plans the per-endpoint containers for a catalog: container id = endpoint id, partition key
    /// and TTL from <see cref="CosmosContainerDefaults"/> (the exact properties the runtime would
    /// have used for lazy creation). Fails when an endpoint id collides with a container id the
    /// message store reserves for its own data.
    /// </summary>
    internal static IReadOnlyList<EndpointContainerSpec> PlanContainers(IPlatform platform)
    {
        var specs = new List<EndpointContainerSpec>();
        foreach (var endpoint in platform.Endpoints)
        {
            try
            {
                CosmosContainerDefaults.EnsureNotReservedEndpointId(endpoint.Id);
            }
            catch (ArgumentException exception)
            {
                throw new CommandException(exception.Message);
            }

            specs.Add(new EndpointContainerSpec(
                endpoint.Id,
                CosmosContainerDefaults.EndpointPartitionKeyPath,
                CosmosContainerDefaults.EndpointContainerDefaultTimeToLive));
        }

        return specs;
    }

    /// <summary>
    /// Creates the missing per-endpoint containers in the conventionally named Cosmos account
    /// (<c>cosmos-{solutionId}-{environment}</c>). Existing containers are left untouched — an
    /// operator may have customized their TTL — so re-runs are idempotent. A no-op for the
    /// SQL Server storage provider.
    /// </summary>
    internal async Task ApplyAsync(TopologyOptions options, StorageProviderChoice storageProvider, CancellationToken cancellationToken)
    {
        if (storageProvider != StorageProviderChoice.Cosmos)
        {
            CliOutput.WriteLine("Skipping Cosmos endpoint container provisioning (storage provider is not cosmos).");
            return;
        }

        var names = NamingConventions.Build(options.SolutionId, options.Environment);
        var specs = PlanContainers(_platformFactory());

        CliOutput.WriteLine($"Provisioning {specs.Count} Cosmos endpoint container(s) in '{names.CosmosAccountName}/{DatabaseName}'...");

        var created = 0;
        var present = 0;
        foreach (var spec in specs)
        {
            if (await ContainerExistsAsync(options.ResourceGroupName, names.CosmosAccountName, spec.ContainerId, cancellationToken).ConfigureAwait(false))
            {
                present++;
                CliOutput.WriteLine($"  {spec.ContainerId}: already present.");
                continue;
            }

            await _az.EnsureSuccessAsync(
                new[]
                {
                    "cosmosdb", "sql", "container", "create",
                    "--resource-group", options.ResourceGroupName,
                    "--account-name", names.CosmosAccountName,
                    "--database-name", DatabaseName,
                    "--name", spec.ContainerId,
                    "--partition-key-path", spec.PartitionKeyPath,
                    "--ttl", spec.DefaultTimeToLive.ToString(CultureInfo.InvariantCulture),
                },
                cancellationToken,
                $"Failed to create the Cosmos container '{spec.ContainerId}' in '{names.CosmosAccountName}/{DatabaseName}'.").ConfigureAwait(false);

            created++;
            CliOutput.WriteLine($"  {spec.ContainerId}: created.");
        }

        CliOutput.WriteLine($"Cosmos endpoint containers: {created} created, {present} already present.");
    }

    private async Task<bool> ContainerExistsAsync(string resourceGroupName, string accountName, string containerId, CancellationToken cancellationToken)
    {
        var value = await _az.CaptureValueAsync(
            new[]
            {
                "cosmosdb", "sql", "container", "exists",
                "--resource-group", resourceGroupName,
                "--account-name", accountName,
                "--database-name", DatabaseName,
                "--name", containerId,
            },
            cancellationToken,
            $"Failed to check whether the Cosmos container '{containerId}' exists in '{accountName}/{DatabaseName}'.").ConfigureAwait(false);

        return bool.TryParse(value.Trim(), out var exists) && exists;
    }
}
