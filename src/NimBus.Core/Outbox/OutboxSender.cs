using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Outbox
{
    /// <summary>
    /// An ISender decorator that writes messages to the transactional outbox
    /// instead of sending them directly to Service Bus.
    /// When configured, PublisherClient uses this sender transparently.
    /// </summary>
    public class OutboxSender : ISender
    {
        private readonly IOutbox _outbox;

        public OutboxSender(IOutbox outbox)
        {
            _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        }

        public async Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            var outboxMessage = ToOutboxMessage(message, messageEnqueueDelay);
            using var activity = StartEnqueueSpan(outboxMessage.To, outboxMessage.EventTypeId, count: 1);
            await _outbox.StoreAsync(outboxMessage, cancellationToken);
            NimBusMeters.OutboxEnqueued.Add(1, BuildEnqueueTags(outboxMessage.To, outboxMessage.EventTypeId));
        }

        public async Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            var outboxMessages = messages.Select(m => ToOutboxMessage(m, messageEnqueueDelay)).ToList();
            var representative = outboxMessages.FirstOrDefault();
            using var activity = StartEnqueueSpan(representative?.To, representative?.EventTypeId, count: outboxMessages.Count);
            await _outbox.StoreBatchAsync(outboxMessages, cancellationToken);
            // Group by (endpoint, event type) so counter increments carry both
            // the nimbus.endpoint and nimbus.event_type dimensions correctly when
            // a single batch crosses endpoints or event types.
            foreach (var grouped in outboxMessages.GroupBy(m => (m.To, m.EventTypeId)))
            {
                NimBusMeters.OutboxEnqueued.Add(grouped.Count(), BuildEnqueueTags(grouped.Key.To, grouped.Key.EventTypeId));
            }
        }

        public async Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
        {
            // In SqlOwnedDueTime mode the provider assigns a real local sequence, so
            // even the legacy bridge can return it (still prefer the handle API).
            if (DueTimeModeActive && _outbox is IScheduledOutbox scheduledOutbox)
            {
                var scheduledRow = ToOutboxMessage(message, 0);
                scheduledRow.ScheduledEnqueueTimeUtc = scheduledEnqueueTime.UtcDateTime;
                using var scheduledActivity = StartEnqueueSpan(scheduledRow.To, scheduledRow.EventTypeId, count: 1);
                var sequenceNumber = await scheduledOutbox.StoreScheduledAsync(scheduledRow, cancellationToken);
                NimBusMeters.OutboxEnqueued.Add(1, BuildEnqueueTags(scheduledRow.To, scheduledRow.EventTypeId));
                return sequenceNumber;
            }

            var outboxMessage = ToOutboxMessage(message, 0);
            outboxMessage.ScheduledEnqueueTimeUtc = scheduledEnqueueTime.UtcDateTime;
            using var activity = StartEnqueueSpan(outboxMessage.To, outboxMessage.EventTypeId, count: 1);
            await _outbox.StoreAsync(outboxMessage, cancellationToken);
            NimBusMeters.OutboxEnqueued.Add(1, BuildEnqueueTags(outboxMessage.To, outboxMessage.EventTypeId));
            // Outbox returns 0 because the real sequence number is only assigned by
            // Service Bus when OutboxDispatcher forwards the message. This means
            // CancelScheduledMessage cannot work in outbox mode.
            return 0L;
        }

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default)
        {
            // NotSupported in ALL modes: the long alone cannot carry the timeout
            // identity and the SQL cancellation CAS requires both (spec 025). The
            // documented migration is the handle API.
            throw new NotSupportedException(
                "Cancelling scheduled messages by sequence number is not supported when using the transactional outbox. " +
                "Use the handle-based CancelScheduled API (spec 025); the sequence number alone cannot carry the timeout identity.");
        }

        /// <summary>
        /// Stores a scheduled row through <see cref="IScheduledOutbox"/> and returns a
        /// provider-local handle. Requires a provider that supports scheduled-message
        /// handles with its SqlOwnedDueTime mode active; nothing is stored before that
        /// capability/mode validation runs.
        /// </summary>
        public async Task<ScheduledMessageHandle> ScheduleMessageWithHandle(
            IMessage message,
            DateTimeOffset scheduledEnqueueTime,
            CancellationToken cancellationToken = default)
        {
            if (message is null) throw new ArgumentNullException(nameof(message));
            var timeoutId = message.ScheduledMessageId ?? message.MessageId;
            ScheduledMessageHandle.ValidateTimeoutId(timeoutId, nameof(message));
            if (_outbox is not IScheduledOutbox scheduledOutbox)
            {
                throw new NotSupportedException(
                    "The configured outbox provider does not support scheduled-message handles (IScheduledOutbox).");
            }

            var outboxMessage = ToOutboxMessage(message, 0);
            outboxMessage.ScheduledEnqueueTimeUtc = scheduledEnqueueTime.UtcDateTime;
            using var activity = StartEnqueueSpan(outboxMessage.To, outboxMessage.EventTypeId, count: 1);
            // StoreScheduledAsync validates the delivery mode before storing anything.
            var sequenceNumber = await scheduledOutbox.StoreScheduledAsync(outboxMessage, cancellationToken);
            NimBusMeters.OutboxEnqueued.Add(1, BuildEnqueueTags(outboxMessage.To, outboxMessage.EventTypeId));
            var handle = new ScheduledMessageHandle(timeoutId, sequenceNumber, ScheduledMessageHandleKind.SqlOutboxSequenceNumber);
            if (sequenceNumber <= 0)
            {
                // Same invariant the ISender default bridge enforces: the public
                // contract is a POSITIVE sequence (ScheduledMessageHandle.Validate).
                // A custom IScheduledOutbox that returns 0 would otherwise hand back
                // a handle every cancel path rejects, leaving the workflow with a
                // timeout it can never cancel. Fail here, where the caller can react.
                throw new InvalidOperationException(
                    $"The outbox provider returned a non-positive sequence number ({sequenceNumber}) for timeout '{timeoutId}'. " +
                    "StoreScheduledAsync must return the provider-assigned scheduled row sequence.");
            }

            handle.Validate(nameof(message));
            return handle;
        }

        /// <summary>
        /// Cancels a scheduled outbox row by handle via the provider's CAS. The
        /// provider matches sequence AND TimeoutId AND scheduled-ness, so a
        /// forged or mistyped handle affects zero rows and returns NotFound.
        /// </summary>
        public Task<ScheduledMessageCancellationOutcome> CancelScheduledMessage(
            ScheduledMessageHandle handle,
            CancellationToken cancellationToken = default)
        {
            if (handle is null) throw new ArgumentNullException(nameof(handle));
            handle.Validate(nameof(handle));
            if (handle.Kind != ScheduledMessageHandleKind.SqlOutboxSequenceNumber)
            {
                throw new InvalidOperationException(
                    $"A {handle.Kind} handle cannot be cancelled through the outbox sender. " +
                    "Use the same endpoint-bound publisher configuration that created the handle.");
            }

            if (_outbox is not IScheduledOutbox scheduledOutbox)
            {
                throw new NotSupportedException(
                    "The configured outbox provider does not support scheduled-message handles (IScheduledOutbox).");
            }

            return scheduledOutbox.CancelScheduledAsync(handle, cancellationToken);
        }

        private bool DueTimeModeActive =>
            _outbox is IOutboxDispatchCoordinator { DueTimeDispatchActive: true };

        private static OutboxMessage ToOutboxMessage(IMessage message, int messageEnqueueDelay)
        {
            var (traceParent, traceState) = W3CMessagePropagator.CaptureCurrent();
            return new OutboxMessage
            {
                Id = Guid.NewGuid().ToString(),
                MessageId = message.MessageId,
                To = message.To,
                EventTypeId = message.EventTypeId ?? message.MessageContent?.EventContent?.EventTypeId,
                SessionId = message.SessionId,
                CorrelationId = message.CorrelationId,
                Payload = JsonConvert.SerializeObject(message),
                EnqueueDelayMinutes = messageEnqueueDelay,
                CreatedAtUtc = DateTime.UtcNow,
                DispatchedAtUtc = null,
                TraceParent = traceParent,
                TraceState = traceState
            };
        }

        private static Activity StartEnqueueSpan(string endpoint, string eventTypeId, int count)
        {
            var activity = NimBusActivitySources.Outbox.StartActivity("NimBus.Outbox.Enqueue", ActivityKind.Internal);
            if (activity is null) return null;
            if (!string.IsNullOrEmpty(endpoint))
                activity.SetTag(MessagingAttributes.NimBusEndpoint, endpoint);
            if (!string.IsNullOrEmpty(eventTypeId))
                activity.SetTag(MessagingAttributes.NimBusEventType, eventTypeId);
            if (count > 1)
                activity.SetTag(MessagingAttributes.NimBusOutboxBatchSize, count);
            return activity;
        }

        private static KeyValuePair<string, object?>[] BuildEnqueueTags(string? endpoint, string? eventTypeId)
        {
            var tags = new List<KeyValuePair<string, object?>>(2);
            if (!string.IsNullOrEmpty(endpoint))
                tags.Add(new KeyValuePair<string, object?>(MessagingAttributes.NimBusEndpoint, endpoint));
            if (!string.IsNullOrEmpty(eventTypeId))
                tags.Add(new KeyValuePair<string, object?>(MessagingAttributes.NimBusEventType, eventTypeId));
            return tags.ToArray();
        }
    }
}
