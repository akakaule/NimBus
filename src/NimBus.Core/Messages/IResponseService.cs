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
    }
}
