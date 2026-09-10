using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.MessageStore;

/// <summary>Administrative access to containers in the configured NimBus Cosmos database.</summary>
public interface ICosmosContainerAdmin
{
    /// <summary>Lists all container identifiers in the configured database.</summary>
    Task<IReadOnlyList<string>> ListContainerIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes a container by its exact identifier.</summary>
    Task<bool> DeleteContainerAsync(string containerId, CancellationToken cancellationToken = default);
}
