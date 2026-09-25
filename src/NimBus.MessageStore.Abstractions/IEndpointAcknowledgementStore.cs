using System.Collections.Generic;
using System.Threading.Tasks;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Storage for Monitor acknowledgements — one record per endpoint, shared by every
/// client. Implemented per storage provider.
/// </summary>
public interface IEndpointAcknowledgementStore
{
    /// <summary>Every stored acknowledgement, expired ones included (expiry is the caller's policy).</summary>
    Task<IReadOnlyList<EndpointAcknowledgement>> GetEndpointAcknowledgements();

    /// <summary>
    /// Upserts the acknowledgement for <see cref="EndpointAcknowledgement.EndpointId"/>,
    /// replacing any existing one for that endpoint.
    /// </summary>
    /// <param name="acknowledgement">The record to store.</param>
    Task SetEndpointAcknowledgement(EndpointAcknowledgement acknowledgement);

    /// <summary>
    /// Removes the endpoint's acknowledgement. When <paramref name="expectedAcknowledgementId"/>
    /// is given, removes it only if the stored record still carries that
    /// <see cref="EndpointAcknowledgement.AcknowledgementId"/>, so a clean-up decided on a
    /// stale read never deletes a newer acknowledgement.
    /// </summary>
    /// <param name="endpointId">The endpoint whose acknowledgement to remove.</param>
    /// <param name="expectedAcknowledgementId">Optional concurrency token; null removes unconditionally.</param>
    /// <returns>True when a record was removed.</returns>
    Task<bool> RemoveEndpointAcknowledgement(string endpointId, string? expectedAcknowledgementId = null);
}
