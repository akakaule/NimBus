using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Messages
{
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
