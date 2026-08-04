using System;

namespace NimBus.Core.Messages
{
    /// <summary>
    /// Identifies which transport authority assigned the sequence number inside a
    /// <see cref="ScheduledMessageHandle"/>. A handle is only valid with the same
    /// endpoint-bound publisher configuration that created it; a sequence of one
    /// kind is never reinterpreted as the other.
    /// </summary>
    public enum ScheduledMessageHandleKind
    {
        /// <summary>The sequence number was assigned by Azure Service Bus native scheduling (direct send path).</summary>
        BrokerSequenceNumber = 0,

        /// <summary>The sequence number is a SQL-outbox-local identity assigned when the scheduled row was stored.</summary>
        SqlOutboxSequenceNumber = 1,
    }

    /// <summary>
    /// Opaque handle for a scheduled message, returned by the scheduling APIs and
    /// required to cancel the schedule. <see cref="TimeoutId"/> is the logical
    /// timeout identity: it equals the deterministic MessageId of the timeout's
    /// first delivery and the <c>ScheduledMessageId</c> marker on all deliveries.
    /// Callers must persist the handle they receive; NimBus never reconstructs or
    /// looks up a handle from <see cref="TimeoutId"/> alone.
    /// </summary>
    /// <param name="TimeoutId">The logical timeout identity (never blank, at most 128 characters).</param>
    /// <param name="SequenceNumber">
    /// Transport-assigned sequence number. A Service Bus sequence in direct mode,
    /// a SQL-outbox-local sequence in outbox mode. Positive in both cases.
    /// </param>
    /// <param name="Kind">The authority that assigned <paramref name="SequenceNumber"/>.</param>
    public sealed record ScheduledMessageHandle(
        string TimeoutId,
        long SequenceNumber,
        ScheduledMessageHandleKind Kind)
    {
        /// <summary>
        /// Maximum length of a <see cref="TimeoutId"/> — the Azure Service Bus
        /// MessageId limit, because the TimeoutId is stamped as the first
        /// delivery's transport MessageId.
        /// </summary>
        public const int MaxTimeoutIdLength = 128;

        /// <summary>
        /// Validates a timeout identity at the public boundary: nonblank and at
        /// most <see cref="MaxTimeoutIdLength"/> characters.
        /// </summary>
        /// <param name="timeoutId">The candidate timeout identity.</param>
        /// <param name="paramName">The caller's parameter name for exception reporting.</param>
        public static void ValidateTimeoutId(string timeoutId, string paramName)
        {
            if (string.IsNullOrWhiteSpace(timeoutId))
                throw new ArgumentException("A nonblank timeout ID is required.", paramName);
            if (timeoutId.Length > MaxTimeoutIdLength)
                throw new ArgumentException(
                    $"The timeout ID must be at most {MaxTimeoutIdLength} characters (it becomes the transport MessageId).",
                    paramName);
        }

        /// <summary>
        /// Validates the handle's shape: nonblank <see cref="TimeoutId"/>, positive
        /// <see cref="SequenceNumber"/>, and a defined <see cref="Kind"/>. Shape
        /// validation only — TimeoutId↔sequence pair validation is enforced solely
        /// where it is enforceable (the SQL outbox provider).
        /// </summary>
        /// <param name="paramName">The caller's parameter name for exception reporting.</param>
        public void Validate(string paramName)
        {
            ValidateTimeoutId(TimeoutId, paramName);
            if (SequenceNumber <= 0)
                throw new ArgumentOutOfRangeException(paramName, SequenceNumber,
                    "The handle's sequence number must be positive.");
            if (Kind != ScheduledMessageHandleKind.BrokerSequenceNumber
                && Kind != ScheduledMessageHandleKind.SqlOutboxSequenceNumber)
            {
                throw new ArgumentOutOfRangeException(paramName, Kind, "Undefined scheduled-message handle kind.");
            }
        }
    }
}
