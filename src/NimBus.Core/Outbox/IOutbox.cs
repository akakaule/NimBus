using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Outbox
{
    /// <summary>
    /// Abstraction for a transactional outbox store.
    /// Messages are written to the outbox within the same transaction as business data,
    /// then dispatched to Service Bus by a background process.
    /// </summary>
    public interface IOutbox
    {
        /// <summary>
        /// Stores a message in the outbox.
        /// This should be called within the same database transaction as business logic.
        /// </summary>
        Task StoreAsync(OutboxMessage message, CancellationToken cancellationToken = default);

        /// <summary>
        /// Stores multiple messages in the outbox atomically.
        /// </summary>
        Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves pending (undispatched) messages for dispatch, ordered by creation time.
        /// </summary>
        /// <param name="batchSize">Maximum number of messages to retrieve.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Pending outbox messages.</returns>
        Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default);

        /// <summary>
        /// Marks a message as dispatched after successful send to Service Bus.
        /// </summary>
        Task MarkAsDispatchedAsync(string id, CancellationToken cancellationToken = default);

        /// <summary>
        /// Marks multiple messages as dispatched.
        /// </summary>
        Task MarkAsDispatchedAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default);
    }
}
