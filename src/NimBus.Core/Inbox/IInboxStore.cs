using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Inbox;

/// <summary>
/// Stores identifiers of messages that completed event-handler processing successfully.
/// </summary>
/// <remarks>
/// The deduplication key is the (<c>endpointId</c>, <c>messageId</c>) pair. NimBus fan-out
/// forwards copies of one published message — with the same broker <c>MessageId</c> — to every
/// subscribed endpoint, so endpoints that share one physical store must not observe each other's
/// records: a record written for one endpoint must never mark the message processed for another.
/// </remarks>
public interface IInboxStore
{
    /// <summary>
    /// Determines whether a message has already completed processing on the given endpoint.
    /// </summary>
    /// <param name="endpointId">The consuming endpoint the deduplication key is scoped to.</param>
    /// <param name="messageId">The message identifier used as the deduplication key.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns><see langword="true"/> when the identifier has been recorded for the endpoint; otherwise, <see langword="false"/>.</returns>
    Task<bool> HasProcessedAsync(
        string endpointId,
        string messageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a successfully processed message for the given endpoint.
    /// Implementations must treat repeated records as success.
    /// </summary>
    /// <param name="endpointId">The consuming endpoint the deduplication key is scoped to.</param>
    /// <param name="messageId">The message identifier used as the deduplication key.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    Task RecordProcessedAsync(
        string endpointId,
        string messageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the given endpoint's records created before the supplied cutoff.
    /// Retention is configured per subscriber, so cleanup must never cross endpoints:
    /// a short-retention subscriber purging a shared table or container would otherwise
    /// delete another subscriber's still-valid records and reopen its duplicate window.
    /// </summary>
    /// <param name="endpointId">The consuming endpoint whose expired records are removed.</param>
    /// <param name="olderThan">The exclusive creation-time cutoff.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The number of records removed.</returns>
    Task<int> PurgeExpiredAsync(
        string endpointId,
        DateTimeOffset olderThan,
        CancellationToken cancellationToken = default);
}
