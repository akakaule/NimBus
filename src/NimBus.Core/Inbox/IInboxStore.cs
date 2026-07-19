using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Inbox;

/// <summary>
/// Stores identifiers of messages that completed event-handler processing successfully.
/// </summary>
public interface IInboxStore
{
    /// <summary>
    /// Determines whether a message has already completed processing.
    /// </summary>
    /// <param name="messageId">The message identifier used as the deduplication key.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns><see langword="true"/> when the identifier has been recorded; otherwise, <see langword="false"/>.</returns>
    Task<bool> HasProcessedAsync(
        string messageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a successfully processed message. Implementations must treat repeated records as success.
    /// </summary>
    /// <param name="messageId">The message identifier used as the deduplication key.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    Task RecordProcessedAsync(
        string messageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes records created before the supplied cutoff.
    /// </summary>
    /// <param name="olderThan">The exclusive creation-time cutoff.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The number of records removed.</returns>
    Task<int> PurgeExpiredAsync(
        DateTimeOffset olderThan,
        CancellationToken cancellationToken = default);
}
