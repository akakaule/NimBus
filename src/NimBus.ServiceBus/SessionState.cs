using System;
using System.Collections.Generic;

namespace NimBus.Core.Messages;

public class SessionState
{
    private List<long> _legacyDeferredSequenceNumbers = new();

    /// <summary>
    /// Legacy: sequence numbers of messages parked with the Azure Service Bus defer API, which
    /// NimBus no longer writes or drains (spec 027 §3). The value still round-trips so legacy
    /// state is not lost, but it no longer blocks the session.
    /// </summary>
    [Obsolete("Legacy Service Bus defer state; no longer written or drained (spec 027 §3). Drain legacy deferred messages with the nb CLI. Removed in the next major version.")]
    public List<long> DeferredSequenceNumbers
    {
        get => _legacyDeferredSequenceNumbers;
        set => _legacyDeferredSequenceNumbers = value ?? new List<long>();
    }

    public string BlockedByEventId { get; set; }

    /// <summary>
    /// Count of messages deferred to the separate deferred subscription.
    /// </summary>
    public int DeferredCount { get; set; }

    /// <summary>
    /// Next sequence number to assign for ordering deferred messages.
    /// </summary>
    public int NextDeferralSequence { get; set; }

    public bool IsEmpty() =>
        BlockedByEventId == null &&
        _legacyDeferredSequenceNumbers.Count == 0 &&
        DeferredCount == 0;

    /// <summary>
    /// Returns true if there are any deferred messages (legacy or deferred-subscription).
    /// </summary>
    public bool HasDeferredMessages() =>
        DeferredCount > 0 || _legacyDeferredSequenceNumbers.Count > 0;
}
