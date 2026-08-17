using System;
using Newtonsoft.Json;

namespace NimBus.MessageStore.States;

/// <summary>
/// Platform-wide heartbeat schedule. A single record: one row on SQL Server, one
/// document with a fixed id on Cosmos. A store that has never been written returns
/// these defaults rather than null.
/// </summary>
public class HeartbeatSettings
{
    /// <summary>Fixed id of the singleton record.</summary>
    public const string SingletonId = "HeartbeatSettings";

    /// <summary>Record id. Always <see cref="SingletonId"/>.</summary>
    [JsonProperty(PropertyName = "id")]
    public string Id { get; set; } = SingletonId;

    /// <summary>Whether the scheduled endpoint fan-out runs. Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>Seconds between scheduled fan-outs.</summary>
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>Seconds a probe may stay Pending before the sweep settles it to Off.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// When the last fan-out was claimed. Owned by <c>TryClaimHeartbeatSend</c> — a
    /// settings write that carries no value leaves the stored one untouched, so an
    /// operator edit never resets the schedule.
    /// </summary>
    public DateTime? LastSentAtUtc { get; set; }

    /// <summary>
    /// When durable heartbeat history was last claimed for folding. This claim is
    /// independent of <see cref="Enabled"/> so manual probes are folded too.
    /// </summary>
    public DateTime? LastHeartbeatFoldAtUtc { get; set; }
}
