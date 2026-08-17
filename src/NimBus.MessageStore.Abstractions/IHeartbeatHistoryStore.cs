using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.Abstractions;

/// <summary>Durable daily heartbeat history and endpoint outage transitions.</summary>
public interface IHeartbeatHistoryStore
{
    /// <summary>
    /// Gets a value indicating whether the provider expires heartbeat history without an explicit prune call.
    /// </summary>
    bool PrunesHeartbeatHistoryAutomatically => false;

    /// <summary>Gets uptime days on or after <paramref name="fromDayUtc"/>.</summary>
    Task<List<HeartbeatUptimeDay>> GetHeartbeatUptimeDays(DateTime fromDayUtc);

    /// <summary>Replaces uptime days by endpoint/day key.</summary>
    Task<bool> UpsertHeartbeatUptimeDays(IEnumerable<HeartbeatUptimeDay> days);

    /// <summary>Gets gaps that overlap the window beginning at <paramref name="fromUtc"/>.</summary>
    Task<List<HeartbeatGap>> GetHeartbeatGaps(DateTime fromUtc);

    /// <summary>Replaces gaps by endpoint/start key.</summary>
    Task<bool> UpsertHeartbeatGaps(IEnumerable<HeartbeatGap> gaps);

    /// <summary>Atomically claims one fleet history fold when the previous claim is due.</summary>
    Task<bool> TryClaimHeartbeatHistoryFold(DateTime dueBefore);

    /// <summary>Removes history wholly older than <paramref name="cutoffUtc"/>.</summary>
    Task PruneHeartbeatHistory(DateTime cutoffUtc);
}
