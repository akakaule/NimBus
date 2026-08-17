using System.Collections.Generic;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.Abstractions;

/// <summary>Rows changed by one pure heartbeat-history fold.</summary>
public sealed class HeartbeatFoldResult
{
    /// <summary>Daily rows that must be replaced.</summary>
    public IReadOnlyList<HeartbeatUptimeDay> Days { get; init; } = [];

    /// <summary>Gap rows opened or closed by the fold.</summary>
    public IReadOnlyList<HeartbeatGap> Gaps { get; init; } = [];
}
