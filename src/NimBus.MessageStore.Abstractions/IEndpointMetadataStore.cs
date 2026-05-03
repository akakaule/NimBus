using System.Collections.Generic;
using System.Threading.Tasks;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Storage operations for endpoint metadata, ownership, and heartbeat state.
/// Implemented per storage provider.
/// </summary>
public interface IEndpointMetadataStore
{
    Task<EndpointMetadata> GetEndpointMetadata(string endpointId);
    Task<List<EndpointMetadata>> GetMetadatas();
    Task<List<EndpointMetadata>?> GetMetadatas(IEnumerable<string> endpointIds);
    Task<List<EndpointMetadata>> GetMetadatasWithEnabledHeartbeat();
    Task<bool> SetEndpointMetadata(EndpointMetadata endpointMetadata);
    Task EnableHeartbeatOnEndpoint(string endpointId, bool enable);
    Task<bool> SetHeartbeat(Heartbeat heartbeat, string endpointId);
}
