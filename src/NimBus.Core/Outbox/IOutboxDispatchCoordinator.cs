using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Outbox
{
    /// <summary>
    /// Optional companion to <see cref="IOutbox"/> exposing the due-time
    /// claim/fence/checkpoint dispatch protocol (spec 025). Registered
    /// unconditionally by providers that support it; <see cref="OutboxDispatcher"/>
    /// runs the protocol iff <see cref="DueTimeDispatchActive"/> is true and
    /// otherwise keeps the legacy GetPendingAsync/MarkAsDispatchedAsync flow.
    /// </summary>
    public interface IOutboxDispatchCoordinator
    {
        /// <summary>
        /// Floor for the configured usable send window
        /// (SendLeaseDuration - SendLeaseSafetyMargin). Options validation
        /// guarantees the window is at least this large, so a fence round trip
        /// can only consume the whole budget while the database is unhealthy —
        /// and the dispatcher then re-fences instead of instantly timing out
        /// the send.
        /// </summary>
        public static readonly TimeSpan MinimumUsableSendWindow = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Provider-neutral protocol signal: true when the provider owns due-time
        /// dispatch (SqlOwnedDueTime). When false, OutboxDispatcher MUST use the
        /// legacy GetPendingAsync/MarkAsDispatchedAsync flow even though a
        /// coordinator is registered.
        /// </summary>
        bool DueTimeDispatchActive { get; }

        /// <summary>
        /// Configured usable send window: SendLeaseDuration minus
        /// SendLeaseSafetyMargin. The dispatcher bounds each send attempt to this
        /// window minus the monotonic time elapsed since the start-fence call was
        /// initiated — a clock-skew-immune conservative lower bound on the true
        /// remaining lease. Always at least <see cref="MinimumUsableSendWindow"/>
        /// (enforced by provider options validation).
        /// </summary>
        TimeSpan UsableSendWindow { get; }

        /// <summary>
        /// Atomically claims up to batchSize due, unblocked rows for the given
        /// owner, applying due-time eligibility, session ordering, and
        /// session-head predicates.
        /// </summary>
        Task<IReadOnlyList<OutboxMessage>> ClaimDueAsync(Guid claimId, int batchSize, CancellationToken cancellationToken = default);

        /// <summary>
        /// Dispatch-start fence and lease renewal: conditionally sets the first
        /// DispatchStartedAtUtc (written only when null), extends the owner's
        /// lease to SYSUTCDATETIME() + SendLeaseDuration, and returns the
        /// SQL-computed lease deadline (UTC). Owner-idempotent: re-invocation by
        /// the owning claim on a started, non-cancelled, non-terminal row renews
        /// the lease and returns a fresh deadline. Returns null when the fence is
        /// lost (cancelled, stale owner, or expired lease reclaimed by another
        /// worker). The returned deadline is the authoritative server-side
        /// reclaim boundary for other workers; callers log it but never compare
        /// it against the client clock.
        /// </summary>
        Task<DateTime?> TryStartDispatchAsync(string outboxMessageId, Guid claimId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Owned checkpoint: marks the row dispatched iff the caller still owns
        /// it. Returns false (no-op) for a stale owner.
        /// </summary>
        Task<bool> TryCompleteAsync(string outboxMessageId, Guid claimId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Releases an owned, not-yet-started claim so the row is immediately
        /// reclaimable. A stale owner's release affects zero rows.
        /// </summary>
        Task ReleaseClaimAsync(string outboxMessageId, Guid claimId, CancellationToken cancellationToken = default);
    }
}
