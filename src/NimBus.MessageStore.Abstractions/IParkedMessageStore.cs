using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Storage contract for the transport-agnostic park-and-replay primitive that
/// implements deferred-by-session in <c>NimBus.Core</c> rather than at the
/// transport layer.
///
/// A "parked message" is a complete <see cref="MessageEntity"/> envelope
/// (serialized to JSON on the wire) tagged with the originating endpoint, the
/// application-level session key, and a monotonic per-session sequence number.
/// When a session blocks, subsequent messages for that session are parked here;
/// when the session unblocks, <c>PortableDeferredMessageProcessor</c> drains the
/// active parked rows in <c>ParkSequence ASC</c> order and replays them through
/// <c>ISender</c>.
///
/// All operations are keyed by <c>(EndpointId, MessageId)</c> for idempotency
/// and <c>(EndpointId, SessionKey, ParkSequence)</c> for ordering. Implementations
/// MUST be safe for concurrent use across multiple receivers parking concurrently
/// for the same or different sessions.
///
/// See <c>docs/specs/003-rabbitmq-transport/deferred-by-session-design.md</c>
/// for the design context.
/// </summary>
public interface IParkedMessageStore
{
    /// <summary>
    /// Parks the supplied message at <c>(EndpointId, SessionKey)</c>, allocating
    /// a monotonic <see cref="ParkedMessage.ParkSequence"/> from
    /// <see cref="ISessionStateStore.GetNextDeferralSequenceAndIncrement"/>.
    /// Idempotent on <c>(EndpointId, MessageId)</c>: re-parking the same message
    /// is a no-op and returns the previously-assigned sequence.
    /// </summary>
    /// <returns>
    /// The sequence number assigned to this parked message (or the existing
    /// sequence if the message was already parked).
    /// </returns>
    Task<long> ParkAsync(ParkedMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns active parked messages (neither replayed nor skipped) for the
    /// session whose <see cref="ParkedMessage.ParkSequence"/> is strictly
    /// greater than <paramref name="afterSequence"/>, ordered ascending. Used by
    /// the replay loop's batch-fetch.
    /// </summary>
    Task<IReadOnlyList<ParkedMessage>> GetActiveAsync(
        string endpointId,
        string sessionKey,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a parked row as replayed (terminal state). Idempotent: calling
    /// twice for the same <paramref name="messageId"/> leaves the row in the
    /// replayed state with the first call's timestamp.
    /// </summary>
    Task MarkReplayedAsync(string endpointId, string messageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the supplied parked rows as skipped (terminal state). Used by the
    /// operator skip-parked affordance in the WebApp. Idempotent on each row.
    /// </summary>
    Task MarkSkippedAsync(
        string endpointId,
        string sessionKey,
        IReadOnlyList<string> messageIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Increments a per-row replay-attempt counter. Returns the new attempt
    /// count after the increment. Used by the dead-letter-after-N-failed-replays
    /// path; a row whose counter exceeds the configured threshold is moved to
    /// the <c>ParkedDeadLettered</c> state and surfaced separately in
    /// <c>nimbus-ops</c>.
    /// </summary>
    Task<int> IncrementReplayAttemptAsync(
        string endpointId,
        string messageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a parked row as dead-lettered after exceeding the replay-attempt
    /// threshold. Distinct from <see cref="MarkSkippedAsync"/> — dead-lettered
    /// rows surface in the operator UI as failures requiring attention.
    /// </summary>
    Task MarkDeadLetteredAsync(
        string endpointId,
        string messageId,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the count of non-terminal parked messages for the session — i.e.
    /// rows with neither <c>ReplayedAtUtc</c>, <c>SkippedAtUtc</c>, nor
    /// <c>DeadLetteredAtUtc</c> set. Used as the reconciliation source for the
    /// counter held in <see cref="ISessionStateStore.GetActiveParkCount"/>.
    /// </summary>
    Task<int> CountActiveAsync(
        string endpointId,
        string sessionKey,
        CancellationToken cancellationToken = default);
}
