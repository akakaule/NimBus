using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Liveness state for the platform's own services (currently the Resolver),
/// kept apart from endpoint heartbeats. Implemented per storage provider.
/// </summary>
public interface IServiceHealthStore
{
    /// <summary>Every known service row, ordered by service id.</summary>
    Task<List<ServiceHealth>> GetServiceHealth();

    /// <summary>
    /// Atomically claims the next probe for <paramref name="serviceId"/> and marks it
    /// in flight under <paramref name="probeMessageId"/>. Returns false when another
    /// (scaled-out) instance already claimed it, or when the previous probe was sent
    /// after <paramref name="dueBefore"/> — so exactly one caller sends per interval.
    /// Creates the row on first use.
    /// </summary>
    /// <param name="serviceId">The platform service to probe.</param>
    /// <param name="dueBefore">A probe is due only when the previous one was sent at or before this instant.</param>
    /// <param name="probeMessageId">Correlation id of the probe about to be sent.</param>
    /// <returns>True when this caller owns the send.</returns>
    Task<bool> TryClaimServiceProbe(string serviceId, DateTime dueBefore, string probeMessageId);

    /// <summary>
    /// Settles the in-flight probe for the given service with the outcome the service
    /// reported, clearing the claim. Creates the row if a response arrives before any
    /// claim. Never touches <see cref="ServiceHealth.LastProbeSentUtc"/> — that field is
    /// owned by the claim, so an answer must not reset the send schedule.
    /// </summary>
    /// <param name="serviceHealth">The settled outcome to store.</param>
    /// <returns>True when the row was written.</returns>
    Task<bool> SetServiceHealth(ServiceHealth serviceHealth);

    /// <summary>
    /// Settles every probe still in flight that was sent at or before
    /// <paramref name="cutoffUtc"/> to Off. Returns the affected service ids.
    /// </summary>
    /// <param name="cutoffUtc">Probes sent at or before this instant have timed out.</param>
    /// <returns>The service ids that were swept.</returns>
    Task<List<string>> SweepTimedOutServiceProbes(DateTime cutoffUtc);
}
