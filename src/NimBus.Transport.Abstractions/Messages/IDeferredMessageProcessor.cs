using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Messages
{
    /// <summary>
    /// Interface for processing deferred messages from the non-session subscription.
    /// Promoted into <c>NimBus.Transport.Abstractions</c> as part of Phase 6.1;
    /// namespace stays <c>NimBus.Core.Messages</c> and a
    /// <c>[TypeForwardedTo]</c> in <c>NimBus.Core</c> preserves source
    /// compatibility for existing consumers.
    /// </summary>
    public interface IDeferredMessageProcessor
    {
        /// <summary>
        /// Processes all deferred messages for the specified session.
        /// Messages are retrieved from the non-session deferred subscription,
        /// filtered by OriginalSessionId, sorted by DeferralSequence,
        /// and re-published to the main topic for normal processing.
        /// </summary>
        /// <param name="sessionId">The session ID to process deferred messages for.</param>
        /// <param name="topicName">The topic name to re-publish messages to.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task ProcessDeferredMessagesAsync(string sessionId, string topicName, CancellationToken cancellationToken = default);
    }
}
