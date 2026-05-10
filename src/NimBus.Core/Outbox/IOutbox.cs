using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Outbox
{
    /// <summary>
    /// Optional companion to <see cref="IOutbox"/> that exposes the cheap aggregate
    /// queries the gauge background service uses to publish
    /// <c>nimbus.outbox.pending</c> and <c>nimbus.outbox.dispatch_lag</c>. Implemented
    /// by outbox providers that can answer them without a full table scan; when no
    /// implementation is registered, the gauge service skips the corresponding
    /// observations (FR-052).
    /// </summary>
    public interface IOutboxMetricsQuery
    {
        /// <summary>
        /// Returns the number of outbox rows that have not yet been dispatched.
        /// </summary>
        Task<long> GetPendingCountAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns the <see cref="OutboxMessage.CreatedAtUtc"/> of the oldest pending
        /// row, or <c>null</c> when there is nothing pending.
        /// </summary>
        Task<DateTimeOffset?> GetOldestPendingEnqueuedAtUtcAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Optional companion that exposes deferred-subscription depth metrics for the
    /// gauge background service. Backed by the Service Bus administration client in
    /// production deployments (<c>ActiveMessageCount</c> on the deferred subscription)
    /// and by tests-shaped fakes elsewhere. When no implementation is registered,
    /// <c>nimbus.deferred.pending</c> and <c>nimbus.deferred.blocked_sessions</c> are
    /// not observed (FR-052).
    /// </summary>
    public interface IDeferredMessageMetricsQuery
    {
        /// <summary>
        /// Returns the number of messages currently parked on the deferred
        /// subscription for <paramref name="endpointId"/>.
        /// </summary>
        Task<long> GetDeferredPendingCountAsync(string endpointId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns the number of distinct sessions currently blocked on
        /// <paramref name="endpointId"/>. Optional — implementers that cannot answer
        /// cheaply may return <c>null</c> and the corresponding gauge is skipped.
        /// </summary>
        Task<long?> GetBlockedSessionCountAsync(string endpointId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists every endpoint the gauge service should poll. Empty when nothing
        /// is registered.
        /// </summary>
        IReadOnlyCollection<string> GetEndpointIds();
    }

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
