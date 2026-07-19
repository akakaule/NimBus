using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Messages
{
    public interface IResponseService
    {
        Task SendResolutionResponse(IMessageContext messageContext, CancellationToken cancellationToken = default);
        Task SendSkipResponse(IMessageContext messageContext, CancellationToken cancellationToken = default);
        Task SendErrorResponse(IMessageContext messageContext, Exception exception, CancellationToken cancellationToken = default);

        /// <summary>
        /// Notifies the Resolver that a message was dead-lettered. Sends a response
        /// message routed to the Resolver carrying the dead-letter reason and
        /// formatted exception so the audit record is classified as DeadLettered.
        /// </summary>
        Task SendDeadLetterResponse(IMessageContext messageContext, string reason, Exception exception, CancellationToken cancellationToken = default);
        Task SendDeferralResponse(IMessageContext messageContext, SessionBlockedException exception, CancellationToken cancellationToken = default);
        Task SendContinuationRequestToSelf(IMessageContext deferredMessageContext, CancellationToken cancellationToken = default);

        /// <summary>
        /// Schedules a retry response after the specified delay.
        /// </summary>
        /// <param name="messageContext">The failed message context.</param>
        /// <param name="messageDelay">The precise delay before the retry is enqueued.</param>
        /// <param name="cancellationToken">A token that can cancel the operation.</param>
        /// <remarks>Legacy implementations fall back to the whole-minute overload.</remarks>
        Task SendRetryResponse(IMessageContext messageContext, TimeSpan messageDelay, CancellationToken cancellationToken = default) =>
            SendRetryResponse(messageContext, (int)Math.Ceiling(messageDelay.TotalMinutes), cancellationToken);

        Task SendRetryResponse(IMessageContext messageContext, int messageDelayMinutes, CancellationToken cancellationToken = default);
        Task SendUnsupportedResponse(IMessageContext messageContext, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends a message to the non-session deferred subscription.
        /// Used when the session is blocked and new messages need to be deferred.
        /// </summary>
        Task SendToDeferredSubscription(IMessageContext messageContext, int deferralSequence, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends a ProcessDeferredRequest to trigger processing of deferred messages.
        /// Called when a session is unblocked and there are deferred messages to process.
        /// </summary>
        Task SendProcessDeferredRequest(IMessageContext messageContext, CancellationToken cancellationToken = default);

        /// <summary>
        /// Notifies the Resolver that the handler signalled
        /// <c>HandlerOutcome.PendingHandoff</c>. Carries the supplied
        /// <see cref="HandoffMetadata"/> on the response message; the
        /// <c>ExpectedBy</c> duration is converted to an absolute UTC
        /// deadline at send time.
        /// </summary>
        Task SendPendingHandoffResponse(IMessageContext messageContext, HandoffMetadata handoff, CancellationToken cancellationToken = default);
    }
}
