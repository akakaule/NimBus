using NimBus.Core.Messages;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
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
            await _outbox.StoreAsync(outboxMessage, cancellationToken);
        }

        public async Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            var outboxMessages = messages.Select(m => ToOutboxMessage(m, messageEnqueueDelay)).ToList();
            await _outbox.StoreBatchAsync(outboxMessages, cancellationToken);
        }

        private static OutboxMessage ToOutboxMessage(IMessage message, int messageEnqueueDelay)
        {
            return new OutboxMessage
            {
                Id = Guid.NewGuid().ToString(),
                MessageId = message.MessageId,
                EventTypeId = message.EventTypeId ?? message.MessageContent?.EventContent?.EventTypeId,
                SessionId = message.SessionId,
                CorrelationId = message.CorrelationId,
                Payload = JsonConvert.SerializeObject(message),
                EnqueueDelayMinutes = messageEnqueueDelay,
                CreatedAtUtc = DateTime.UtcNow,
                DispatchedAtUtc = null
            };
        }
    }
}
