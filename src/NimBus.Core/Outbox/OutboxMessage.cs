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
    }
}
