using System;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// A message parked in <see cref="IParkedMessageStore"/> while its session was
/// blocked. The full envelope is carried as <see cref="MessageEnvelopeJson"/>
/// (JSON-serialized <see cref="MessageEntity"/>); fields above the envelope
/// duplicate parts of the envelope for fast indexable lookups (the natural-key
/// idempotency check on <c>MessageId</c>, the ordering on <c>ParkSequence</c>,
/// the per-endpoint operator views).
/// </summary>
public sealed class ParkedMessage
{
    /// <summary>Receiver endpoint that parked the message.</summary>
    public string EndpointId { get; set; } = string.Empty;

    /// <summary>Application-level session key (the receiver's <c>SessionId</c>).</summary>
    public string SessionKey { get; set; } = string.Empty;

    /// <summary>
    /// Monotonic per-<c>(EndpointId, SessionKey)</c> sequence number, allocated
    /// at park time. Replay reads ordered by this column ascending.
    /// </summary>
    public long ParkSequence { get; set; }

    /// <summary>The original transport message id — the natural idempotency key.</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>The originating event id (the unit the WebApp tracks).</summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>
    /// Event-type id; copied to a column / property for operator-UI filtering
    /// without parsing the envelope.
    /// </summary>
    public string EventTypeId { get; set; } = string.Empty;

    /// <summary>
    /// The event id whose failure blocked the session at park time. Stored
    /// for diagnostic visibility in <c>nimbus-ops</c>; may be null/empty if
    /// the block has been cleared since the row was written.
    /// </summary>
    public string? BlockingEventId { get; set; }

    /// <summary>
    /// The full <see cref="MessageEntity"/> envelope, JSON-serialized. Replay
    /// deserializes this back to a <c>Message</c> and sends it through
    /// <c>ISender</c> exactly as if it had just been published.
    /// </summary>
    public string MessageEnvelopeJson { get; set; } = string.Empty;

    /// <summary>UTC wall-clock park time.</summary>
    public DateTime ParkedAtUtc { get; set; }

    /// <summary>
    /// Set when the parked row has been replayed. Mutually exclusive with
    /// <see cref="SkippedAtUtc"/> and <see cref="DeadLetteredAtUtc"/>.
    /// </summary>
    public DateTime? ReplayedAtUtc { get; set; }

    /// <summary>
    /// Set when an operator skipped the parked row. Mutually exclusive with
    /// <see cref="ReplayedAtUtc"/> and <see cref="DeadLetteredAtUtc"/>.
    /// </summary>
    public DateTime? SkippedAtUtc { get; set; }

    /// <summary>
    /// Set when the row exceeded the configured replay-attempt threshold and
    /// was dead-lettered. Mutually exclusive with <see cref="ReplayedAtUtc"/>
    /// and <see cref="SkippedAtUtc"/>.
    /// </summary>
    public DateTime? DeadLetteredAtUtc { get; set; }

    /// <summary>
    /// Reason text for a dead-lettered parked row (typically the last
    /// transport error from the failing replay attempt).
    /// </summary>
    public string? DeadLetterReason { get; set; }

    /// <summary>Number of replay attempts made; incremented per failed replay.</summary>
    public int ReplayAttemptCount { get; set; }
}
