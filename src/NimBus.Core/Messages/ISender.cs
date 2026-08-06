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

        /// <summary>
        /// Schedules a message for delivery at the specified time and returns a
        /// <see cref="ScheduledMessageHandle"/> carrying the logical timeout identity
        /// (the message's <see cref="IMessage.ScheduledMessageId"/>, falling back to its
        /// <see cref="IMessage.MessageId"/>) alongside the transport sequence number.
        /// The default implementation bridges to <see cref="ScheduleMessage"/> and
        /// returns a <see cref="ScheduledMessageHandleKind.BrokerSequenceNumber"/> handle,
        /// which is correct for direct and custom broker-backed senders; outbox senders
        /// override it to return a provider-local handle.
        /// </summary>
        async Task<ScheduledMessageHandle> ScheduleMessageWithHandle(
            IMessage message,
            DateTimeOffset scheduledEnqueueTime,
            CancellationToken cancellationToken = default)
        {
            if (message is null) throw new ArgumentNullException(nameof(message));
            var timeoutId = message.ScheduledMessageId ?? message.MessageId;
            ScheduledMessageHandle.ValidateTimeoutId(timeoutId, nameof(message));
            var sequenceNumber = await ScheduleMessage(message, scheduledEnqueueTime, cancellationToken).ConfigureAwait(false);
            var handle = new ScheduledMessageHandle(timeoutId, sequenceNumber, ScheduledMessageHandleKind.BrokerSequenceNumber);
            if (sequenceNumber <= 0)
            {
                // The public invariant is a POSITIVE sequence (ScheduledMessageHandle.Validate).
                // A sender whose legacy ScheduleMessage returns 0 — an outbox in default
                // mode, or a custom/test sender that never implemented broker scheduling —
                // would otherwise hand back a handle that every cancel path immediately
                // rejects. Fail at schedule time, where the caller can still react, rather
                // than at cancel time, when the workflow is already committed to a timeout.
                throw new InvalidOperationException(
                    $"ScheduleMessage returned a non-positive sequence number ({sequenceNumber}) for timeout '{timeoutId}'. " +
                    "A broker-backed sender must return the transport-assigned scheduled sequence number; " +
                    "providers that cannot must override ScheduleMessageWithHandle with their own handle kind.");
            }

            handle.Validate(nameof(message));
            return handle;
        }

        /// <summary>
        /// Cancels a scheduled message by handle. The default implementation bridges to
        /// <see cref="CancelScheduledMessage(long, CancellationToken)"/> for direct and
        /// custom broker-backed senders: it validates the handle's shape and kind, then
        /// issues the sequence-only broker cancellation and returns
        /// <see cref="ScheduledMessageCancellationOutcome.CancellationRequested"/>.
        /// The broker API cannot verify that <see cref="ScheduledMessageHandle.TimeoutId"/>
        /// matches the sequence — a mismatched pair cancels whatever sequence was supplied
        /// (documented best effort); TimeoutId↔sequence pair validation is enforced only
        /// by the SQL outbox provider, whose override returns the precise outcome.
        /// A handle of the wrong kind is rejected rather than silently reinterpreted.
        /// </summary>
        async Task<ScheduledMessageCancellationOutcome> CancelScheduledMessage(
            ScheduledMessageHandle handle,
            CancellationToken cancellationToken = default)
        {
            if (handle is null) throw new ArgumentNullException(nameof(handle));
            handle.Validate(nameof(handle));
            if (handle.Kind != ScheduledMessageHandleKind.BrokerSequenceNumber)
            {
                throw new InvalidOperationException(
                    $"A {handle.Kind} handle cannot be cancelled through a broker-backed sender. " +
                    "Use the same endpoint-bound publisher configuration that created the handle.");
            }

            await CancelScheduledMessage(handle.SequenceNumber, cancellationToken).ConfigureAwait(false);
            return ScheduledMessageCancellationOutcome.CancellationRequested;
        }
    }
}
