using System.Threading;
using System.Threading.Tasks;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Per-session state operations that used to live on <c>IMessageContext</c> but
/// are conceptually store-backed, not transport-backed: the "is this session
/// currently blocked by a failed message?" flag, and the ordering / count helpers
/// the deferred-message subscription uses.
///
/// Today the SQL Server and Cosmos DB providers persist this state in their own
/// per-session row / document. The transport (Service Bus, RabbitMQ, …) never
/// owns it. Splitting it out of <c>IMessageContext</c> is the prerequisite for a
/// clean transport boundary — see ADR-011 / the Phase 6 RabbitMQ spec.
///
/// State is keyed by <c>(endpointId, sessionId)</c>. Implementations MUST be safe
/// for concurrent use across multiple receivers processing the same session id
/// (e.g. two replicas of the same subscriber pod), even though Service Bus only
/// permits one active lock per session — this is enforced at the transport layer,
/// not the store.
/// </summary>
public interface ISessionStateStore
{
    /// <summary>
    /// Marks the given session as blocked by the supplied event id. Subsequent
    /// messages on this session should not be processed until the block is
    /// released via <see cref="UnblockSession"/>.
    /// </summary>
    Task BlockSession(string endpointId, string sessionId, string eventId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases any block on the given session. No-op if the session is not blocked.
    /// </summary>
    Task UnblockSession(string endpointId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns true if the session is blocked by any event, or has any deferred
    /// messages waiting to be replayed (legacy or new approach).
    /// </summary>
    Task<bool> IsSessionBlocked(string endpointId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns true if the session is blocked specifically by the supplied
    /// event id. Used by the resubmit-original-blocker flow so the receiver
    /// can recognise its own retry attempt and unblock the session.
    /// </summary>
    Task<bool> IsSessionBlockedByThis(string endpointId, string sessionId, string eventId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns true if the session is blocked by some event id (i.e. a previous
    /// failed message marked the session as blocked).
    /// </summary>
    Task<bool> IsSessionBlockedByEventId(string endpointId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the event id currently blocking this session, or <c>null</c> if
    /// the session is not blocked.
    /// </summary>
    Task<string> GetBlockedByEventId(string endpointId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the next deferral sequence number and increments the counter.
    /// Used for ordering messages in the non-session deferred subscription.
    /// </summary>
    Task<int> GetNextDeferralSequenceAndIncrement(string endpointId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Increments the deferred message count in session state.
    /// </summary>
    Task IncrementDeferredCount(string endpointId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decrements the deferred message count in session state.
    /// </summary>
    Task DecrementDeferredCount(string endpointId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current deferred message count from session state.
    /// </summary>
    Task<int> GetDeferredCount(string endpointId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if there are any deferred messages (legacy or new approach).
    /// </summary>
    Task<bool> HasDeferredMessages(string endpointId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets the deferred message count to zero.
    /// Called after all deferred messages have been republished.
    /// </summary>
    Task ResetDeferredCount(string endpointId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the per-session replay checkpoint — the highest park sequence
    /// number that has already been replayed for the session. Initial value is
    /// <c>-1</c> (nothing replayed yet). Used by the portable deferred-by-session
    /// replay loop to drive forward-only progress and resume safely after a
    /// receiver crash. See
    /// <c>docs/specs/003-rabbitmq-transport/deferred-by-session-design.md</c>
    /// for context.
    /// </summary>
    Task<int> GetLastReplayedSequence(string endpointId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Conditional, forward-only update of the replay checkpoint. Applies
    /// <paramref name="newValue"/> if and only if the current stored value
    /// equals <paramref name="expectedCurrent"/>. Returns <c>true</c> if the
    /// update was applied; <c>false</c> if a concurrent replayer has already
    /// advanced past the expected value (caller re-reads the checkpoint and
    /// retries). This is the serialization point that lets two concurrent
    /// replayers race safely without a distributed lock — see
    /// <c>docs/specs/003-rabbitmq-transport/deferred-by-session-design.md</c>
    /// §5.3.
    /// </summary>
    Task<bool> TryAdvanceLastReplayedSequence(string endpointId, string sessionId, int expectedCurrent, int newValue, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the count of non-terminal parked messages for the session — i.e.
    /// messages that have been parked but neither replayed nor skipped. Used as
    /// a hot-path read by receivers and by the replay loop's drained-check; the
    /// implementation typically maintains a counter alongside the other session
    /// state rather than running <c>COUNT(*)</c> against the parked-message
    /// store. See
    /// <c>docs/specs/003-rabbitmq-transport/deferred-by-session-design.md</c>
    /// §11 / §17 for rationale.
    /// </summary>
    Task<int> GetActiveParkCount(string endpointId, string sessionId, CancellationToken cancellationToken = default);
}
