namespace NimBus.Core.Messages
{
    /// <summary>
    /// Result of a scheduled-message cancellation request. Cancellation is an
    /// optimization, never the correctness boundary: application workflow-state
    /// and idempotency guards remain the final authority on whether a timeout's
    /// effects apply.
    /// </summary>
    public enum ScheduledMessageCancellationOutcome
    {
        /// <summary>
        /// The broker cancellation request was accepted (direct mode). Activation
        /// and cancellation are independent broker operations, so the timeout may
        /// still be delivered; this is not proof of prevention.
        /// </summary>
        CancellationRequested = 0,

        /// <summary>
        /// The SQL outbox row was cancelled before dispatch started. An upgraded
        /// dispatcher fleet in SqlOwnedDueTime mode will never send it.
        /// </summary>
        CancelledBeforeDispatch = 1,

        /// <summary>The row was already cancelled; no second mutation occurred.</summary>
        AlreadyCancelled = 2,

        /// <summary>
        /// Dispatch already started (or completed); cancellation can no longer
        /// truthfully claim prevention, even if the broker operation later fails.
        /// </summary>
        TooLate = 3,

        /// <summary>
        /// No scheduled row matched the handle (unknown, rolled back, purged, or
        /// nonscheduled). Zero rows were affected.
        /// </summary>
        NotFound = 4,

        /// <summary>The sender/mode cannot cancel scheduled messages (legacy long-only path in outbox mode).</summary>
        Unsupported = 5,
    }
}
