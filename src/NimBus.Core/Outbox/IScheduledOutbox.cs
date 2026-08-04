using System.Threading;
using System.Threading.Tasks;
using NimBus.Core.Messages;

namespace NimBus.Core.Outbox
{
    /// <summary>
    /// Optional companion to <see cref="IOutbox"/> for providers that can own a
    /// scheduled message's due time durably (spec 025). Implemented by the SQL
    /// Server outbox; registration is unconditional, but both members throw
    /// unless the provider's SqlOwnedDueTime delivery mode is active, so the
    /// handle API cannot silently produce rows an old dispatcher fleet might
    /// mishandle.
    /// </summary>
    public interface IScheduledOutbox
    {
        /// <summary>
        /// Stores a scheduled outbox row inside the ambient transaction (when one
        /// exists) and returns the provider-local sequence number. The message
        /// carries ScheduledMessageId (TimeoutId) and ScheduledEnqueueTimeUtc.
        /// Throws <see cref="System.InvalidOperationException"/> naming the
        /// required mode unless SqlOwnedDueTime is active.
        /// </summary>
        Task<long> StoreScheduledAsync(OutboxMessage message, CancellationToken cancellationToken = default);

        /// <summary>
        /// CAS-cancels a pending scheduled row by handle (sequence + TimeoutId +
        /// scheduled-ness). Honors the ambient transaction. Throws
        /// <see cref="System.InvalidOperationException"/> naming the required mode
        /// unless SqlOwnedDueTime is active.
        /// </summary>
        Task<ScheduledMessageCancellationOutcome> CancelScheduledAsync(ScheduledMessageHandle handle, CancellationToken cancellationToken = default);
    }
}
