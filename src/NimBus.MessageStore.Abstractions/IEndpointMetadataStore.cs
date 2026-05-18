using System.Collections.Generic;
using System.Threading.Tasks;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Storage operations for endpoint metadata and ownership.
/// Implemented per storage provider.
/// </summary>
public interface IEndpointMetadataStore
{
    Task<EndpointMetadata> GetEndpointMetadata(string endpointId);
    Task<List<EndpointMetadata>> GetMetadatas();
    Task<List<EndpointMetadata>?> GetMetadatas(IEnumerable<string> endpointIds);
    Task<bool> SetEndpointMetadata(EndpointMetadata endpointMetadata);
}
