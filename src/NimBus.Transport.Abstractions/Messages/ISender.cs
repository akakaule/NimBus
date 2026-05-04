using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Messages
{
    /// <summary>
    /// Provider-neutral message-sending contract. Promoted into
    /// <c>NimBus.Transport.Abstractions</c> so transport adapters expose the same
    /// surface; namespace stays <c>NimBus.Core.Messages</c> and a
    /// <c>[TypeForwardedTo]</c> in <c>NimBus.Core</c> preserves source
    /// compatibility for existing consumers.
    /// </summary>
    public interface ISender
    {
        Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default);
        Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default);

        /// <summary>
        /// Schedules a message for delivery at the specified time.
        /// Returns a sequence number that can be used to cancel the scheduled message.
        /// </summary>
        Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default);

        /// <summary>
        /// Cancels a previously scheduled message using the sequence number returned by <see cref="ScheduleMessage"/>.
        /// </summary>
        Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default);
    }
}
