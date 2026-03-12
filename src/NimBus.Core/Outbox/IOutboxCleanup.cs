using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Outbox
{
    /// <summary>
    /// Handles cleanup of dispatched outbox messages to prevent unbounded growth.
    /// </summary>
    public interface IOutboxCleanup
    {
        /// <summary>
        /// Purges dispatched messages older than the specified age.
        /// </summary>
        /// <param name="olderThan">Minimum age of dispatched messages to purge.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The number of messages purged.</returns>
        Task<int> PurgeDispatchedAsync(TimeSpan olderThan, CancellationToken cancellationToken = default);
    }
}
