using System;

namespace NimBus.Core.Outbox
{
    /// <summary>
    /// Represents a message stored in the transactional outbox, pending dispatch to Service Bus.
    /// </summary>
    public class OutboxMessage
    {
        /// <summary>
        /// Unique identifier for this outbox entry.
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// The NimBus message ID.
        /// </summary>
        public string MessageId { get; set; }

        /// <summary>
        /// The destination endpoint (topic name). Surfaced as the
        /// <c>nimbus.endpoint</c> tag on outbox enqueue/dispatch metrics and as
        /// the <c>messaging.destination.name</c> attribute on the dispatch span.
        /// Captured at enqueue time so the dispatcher does not have to
        /// deserialize the payload to read it.
        /// </summary>
        public string To { get; set; }

        /// <summary>
        /// The event type identifier.
        /// </summary>
        public string EventTypeId { get; set; }

        /// <summary>
        /// The session ID for ordered delivery.
        /// </summary>
        public string SessionId { get; set; }

        /// <summary>
        /// The correlation ID for tracing.
        /// </summary>
        public string CorrelationId { get; set; }

        /// <summary>
        /// The serialized IMessage payload (JSON).
        /// </summary>
        public string Payload { get; set; }

        /// <summary>
        /// Scheduled enqueue delay in minutes (0 for immediate).
        /// </summary>
        public int EnqueueDelayMinutes { get; set; }

        /// <summary>
        /// When this outbox entry was created.
        /// </summary>
        public DateTime CreatedAtUtc { get; set; }

        /// <summary>
        /// Absolute scheduled delivery time. Null for immediate delivery.
        /// Used by <see cref="ISender.ScheduleMessage"/> via the outbox.
        /// </summary>
        public DateTime? ScheduledEnqueueTimeUtc { get; set; }

        /// <summary>
        /// When this outbox entry was dispatched to Service Bus. Null if pending.
        /// </summary>
        public DateTime? DispatchedAtUtc { get; set; }

        /// <summary>
        /// W3C <c>traceparent</c> captured from <see cref="System.Diagnostics.Activity.Current"/>
        /// at the moment this row was persisted. Used by the dispatcher to attach the original
        /// publish context as an <see cref="System.Diagnostics.ActivityLink"/> on the dispatch
        /// span. Null on rows persisted before W3C capture was wired in.
        /// </summary>
        public string TraceParent { get; set; }

        /// <summary>
        /// W3C <c>tracestate</c> captured alongside <see cref="TraceParent"/>. Null if the
        /// originating activity carried no tracestate.
        /// </summary>
        public string TraceState { get; set; }

        /// <summary>
        /// Provider-local sequence number (SQL IDENTITY). Assigned by the store on
        /// insert; the sequence inside a <see cref="Messages.ScheduledMessageHandle"/>
        /// of kind SqlOutboxSequenceNumber. Zero for providers/rows that predate the
        /// column. Immediate rows also receive values but are not cancellable.
        /// </summary>
        public long OutboxSequenceNumber { get; set; }

        /// <summary>
        /// SQL-assigned insert timestamp — the ordering authority for unscheduled
        /// rows in SqlOwnedDueTime mode (application-stamped
        /// <see cref="CreatedAtUtc"/> never gates or orders dispatch there). Null
        /// on providers/rows that predate the column; default mode ignores it.
        /// </summary>
        public DateTime? StoredAtUtc { get; set; }

        /// <summary>
        /// When the row's cancellation CAS won. Terminal: a cancelled row is never
        /// dispatched by an upgraded dispatcher in SqlOwnedDueTime mode.
        /// </summary>
        public DateTime? CancelledAtUtc { get; set; }

        /// <summary>
        /// When the first dispatch-start fence won. Once set, cancellation reports
        /// TooLate; the row is its session's in-flight slot until it terminalizes.
        /// </summary>
        public DateTime? DispatchStartedAtUtc { get; set; }

        /// <summary>Owner of the row's live dispatch claim; null when unclaimed.</summary>
        public Guid? DispatchClaimId { get; set; }

        /// <summary>SQL-computed lease deadline of the live claim; the server-side reclaim boundary.</summary>
        public DateTime? DispatchClaimedUntilUtc { get; set; }
    }
}
