using NimBus.Core.Messages;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Outbox
{
    /// <summary>
    /// Dispatches pending outbox messages to the real message sender.
    /// This is the core polling logic; wrap in a hosted service for background execution.
    /// </summary>
    public class OutboxDispatcher
    {
        private readonly IOutbox _outbox;
        private readonly ISender _sender;

        public OutboxDispatcher(IOutbox outbox, ISender sender)
        {
            _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
            _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        }

        /// <summary>
        /// Dispatches a batch of pending outbox messages.
        /// </summary>
        /// <param name="batchSize">Maximum number of messages to dispatch in one batch.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The number of messages dispatched.</returns>
        public async Task<int> DispatchPendingAsync(int batchSize = 100, CancellationToken cancellationToken = default)
        {
            var pending = await _outbox.GetPendingAsync(batchSize, cancellationToken);
            if (pending.Count == 0)
                return 0;

            var dispatched = new List<string>();
            foreach (var outboxMessage in pending)
            {
                try
                {
                    var message = JsonConvert.DeserializeObject<Message>(outboxMessage.Payload);
                    await _sender.Send(message, outboxMessage.EnqueueDelayMinutes, cancellationToken);
                    dispatched.Add(outboxMessage.Id);
                }
                catch (Exception)
                {
                    // Stop dispatching on first failure to maintain ordering.
                    // The failed message will be retried on the next poll.
                    break;
                }
            }

            if (dispatched.Count > 0)
            {
                await _outbox.MarkAsDispatchedAsync(dispatched, cancellationToken);
            }

            return dispatched.Count;
        }
    }
}
