using System;
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
    Task<bool> SetEndpointMetadata(EndpointMetadata endpointMetadata);

    /// <summary>
    /// Endpoints that explicitly opted IN to heartbeat probing
    /// (<see cref="EndpointMetadata.IsHeartbeatEnabled"/> == true).
    /// </summary>
    /// <remarks>
    /// This is NOT the fan-out enumeration. The sender walks the platform catalog and
    /// excludes only explicit opt-outs (<c>IsHeartbeatEnabled == false</c>), so an
    /// endpoint with missing or null metadata is probed. Wiring the fan-out to this
    /// method would silently skip every endpoint an operator never touched.
    /// </remarks>
    Task<List<EndpointMetadata>> GetMetadatasWithEnabledHeartbeat();

    /// <summary>
    /// Sets the heartbeat opt-in flag for one endpoint, creating the metadata record
    /// when it does not exist yet.
    /// </summary>
    /// <param name="endpointId">The endpoint to configure.</param>
    /// <param name="enable">True to probe the endpoint, false to leave it out of the fan-out.</param>
    Task EnableHeartbeatOnEndpoint(string endpointId, bool enable);

    /// <summary>
    /// Writes one heartbeat row for an endpoint, keyed by
    /// <see cref="Heartbeat.MessageId"/> so the Pending probe and the answer that
    /// settles it share a row, prunes the endpoint's history, and refreshes the
    /// rollup status on the metadata record.
    /// </summary>
    /// <param name="heartbeat">The probe state to store.</param>
    /// <param name="endpointId">The endpoint the probe belongs to.</param>
    /// <returns>True when the row was written.</returns>
    Task<bool> SetHeartbeat(Heartbeat heartbeat, string endpointId);

    /// <summary>
    /// Settles every Pending heartbeat started at or before <paramref name="cutoffUtc"/>
    /// to Off and refreshes the per-endpoint rollup status. Returns the affected endpoint ids.
    /// </summary>
    /// <param name="cutoffUtc">Probes started at or before this instant have timed out.</param>
    /// <returns>The endpoint ids that were swept.</returns>
    Task<List<string>> SweepTimedOutHeartbeats(DateTime cutoffUtc);

    /// <summary>The platform-wide heartbeat schedule; defaults when nothing has been written.</summary>
    Task<HeartbeatSettings> GetHeartbeatSettings();

    /// <summary>
    /// Upserts the heartbeat schedule. A null <see cref="HeartbeatSettings.LastSentAtUtc"/>
    /// leaves the stored value untouched — that field is owned by
    /// <see cref="TryClaimHeartbeatSend"/>, so an operator edit must not reset the schedule.
    /// </summary>
    /// <param name="settings">The schedule to store.</param>
    /// <returns>True when the record was written.</returns>
    Task<bool> SetHeartbeatSettings(HeartbeatSettings settings);

    /// <summary>
    /// Atomically claims the next scheduled fan-out. Returns false when heartbeats are
    /// disabled, or when the last send happened after <paramref name="dueBefore"/> —
    /// so exactly one scaled-out instance sends per interval.
    /// </summary>
    /// <param name="dueBefore">A fan-out is due only when the previous one ran at or before this instant.</param>
    /// <returns>True when this caller owns the send.</returns>
    Task<bool> TryClaimHeartbeatSend(DateTime dueBefore);

    /// <summary>One row per known endpoint: current status, round-trip and reported SDK version.</summary>
    Task<List<HeartbeatOverviewItem>> GetHeartbeatOverview();
}
