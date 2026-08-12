using System;
using System.Collections.Generic;
using System.Linq;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Status semantics shared by the document-shaped providers (Cosmos DB and the
/// in-memory store), which keep heartbeat rows embedded in the endpoint metadata
/// record. The SQL Server provider implements the same rules in T-SQL; the
/// conformance suite pins both to this behavior.
/// </summary>
public static class HeartbeatRollup
{
    /// <summary>How many heartbeat rows are kept per endpoint.</summary>
    public const int MaxHeartbeatsPerEndpoint = 20;

    /// <summary>
    /// Keeps the newest <see cref="MaxHeartbeatsPerEndpoint"/> rows, returned oldest first.
    /// </summary>
    /// <param name="heartbeats">The endpoint's rows in any order.</param>
    /// <returns>The pruned list, ordered by send time ascending.</returns>
    public static List<Heartbeat> Prune(IEnumerable<Heartbeat> heartbeats)
        => (heartbeats ?? Enumerable.Empty<Heartbeat>())
            .OrderByDescending(h => h.StartTime)
            .ThenByDescending(h => h.EndTime)
            .Take(MaxHeartbeatsPerEndpoint)
            .OrderBy(h => h.StartTime)
            .ToList();

    /// <summary>
    /// Refreshes <see cref="EndpointMetadata.EndpointHeartbeatStatus"/> from the endpoint's
    /// rows. The rollup mirrors the most recent settled probe (On/Off/Unsupported); an
    /// in-flight Pending must not mask the last known outcome, so Pending shows only
    /// before the first settled result.
    /// </summary>
    /// <param name="metadata">The metadata record to update in place.</param>
    public static void Apply(EndpointMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var settled = Latest(metadata.Heartbeats, h => h.EndpointHeartbeatStatus != HeartbeatStatus.Pending);
        if (settled != null)
        {
            metadata.EndpointHeartbeatStatus = settled.EndpointHeartbeatStatus;
        }
        else if (metadata.Heartbeats?.Count > 0)
        {
            metadata.EndpointHeartbeatStatus = HeartbeatStatus.Pending;
        }
    }

    /// <summary>
    /// Projects one metadata record onto an overview row. Status is the last settled
    /// outcome; the response fields come from the last probe that was actually answered,
    /// because a swept, timed-out row carried no response.
    /// </summary>
    /// <param name="metadata">The metadata record to project.</param>
    /// <returns>The overview row for this endpoint.</returns>
    public static HeartbeatOverviewItem BuildOverviewItem(EndpointMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var latest = Latest(metadata.Heartbeats, _ => true);
        var settled = Latest(metadata.Heartbeats, h => h.EndpointHeartbeatStatus != HeartbeatStatus.Pending);
        var responded = Latest(metadata.Heartbeats, h =>
            h.EndpointHeartbeatStatus is HeartbeatStatus.On or HeartbeatStatus.Unsupported);

        return new HeartbeatOverviewItem
        {
            EndpointId = metadata.EndpointId,
            IsHeartbeatEnabled = metadata.IsHeartbeatEnabled,
            MessageId = latest?.MessageId ?? string.Empty,
            LastStartTime = latest?.StartTime,
            LastReceivedTime = responded?.ReceivedTime,
            LastEndTime = responded?.EndTime,
            RoundTripMs = responded == null || responded.StartTime == default || responded.EndTime == default
                ? null
                : (long)(responded.EndTime - responded.StartTime).TotalMilliseconds,
            SdkVersion = responded?.SdkVersion ?? string.Empty,
            Status = settled?.EndpointHeartbeatStatus
                ?? (latest != null ? HeartbeatStatus.Pending : metadata.EndpointHeartbeatStatus)
                ?? HeartbeatStatus.Unknown,
        };
    }

    private static Heartbeat? Latest(List<Heartbeat>? heartbeats, Func<Heartbeat, bool> predicate)
        => heartbeats?
            .Where(predicate)
            .OrderByDescending(h => h.StartTime)
            .ThenByDescending(h => h.EndTime)
            .FirstOrDefault();
}
