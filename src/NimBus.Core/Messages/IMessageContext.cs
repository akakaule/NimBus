using NimBus.Core.Messages.Exceptions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Messages
{
    public interface IReceivedMessage : IMessage
    {
        DateTime EnqueuedTimeUtc { get; }
        new string MessageId { get; }

        new string EventTypeId { get; }
        new string DeadLetterReason { get; }
        new string DeadLetterErrorDescription { get; }
    }

    public interface IMessageContext : IReceivedMessage
    {
        bool IsDeferred { get; }
        Task Complete(CancellationToken cancellationToken = default);

        Task Abandon(TransientException exception);

        Task DeadLetter(string reason, Exception exception = null, CancellationToken cancellationToken = default);

        Task Defer(CancellationToken cancellationToken = default);

        Task DeferOnly(CancellationToken cancellationToken = default);

        Task<IMessageContext> ReceiveNextDeferred(CancellationToken cancellationToken = default);
        Task<IMessageContext> ReceiveNextDeferredWithPop(CancellationToken cancellationToken = default);

        [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
        Task BlockSession(CancellationToken cancellationToken = default);

        [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
        Task UnblockSession(CancellationToken cancellationToken = default);

        [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
        Task<bool> IsSessionBlocked(CancellationToken cancellationToken = default);

        [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
        Task<bool> IsSessionBlockedByThis(CancellationToken cancellationToken = default);

        [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
        Task<bool> IsSessionBlockedByEventId(CancellationToken cancellationToken = default);

        [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
        Task<string> GetBlockedByEventId(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the next deferral sequence number and increments the counter.
        /// Used for ordering messages in the non-session deferred subscription.
        /// </summary>
        [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
        Task<int> GetNextDeferralSequenceAndIncrement(CancellationToken cancellationToken = default);

        /// <summary>
        /// Increments the deferred message count in session state.
        /// </summary>
        [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
        Task IncrementDeferredCount(CancellationToken cancellationToken = default);

        /// <summary>
        /// Decrements the deferred message count in session state.
        /// </summary>
        [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
        Task DecrementDeferredCount(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the current deferred message count from session state.
        /// </summary>
        [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
        Task<int> GetDeferredCount(CancellationToken cancellationToken = default);

        /// <summary>
        /// Checks if there are any deferred messages (legacy or new approach).
        /// </summary>
        [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
        Task<bool> HasDeferredMessages(CancellationToken cancellationToken = default);

        /// <summary>
        /// Resets the deferred message count to zero.
        /// Called after all deferred messages have been republished.
        /// </summary>
        [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
        Task ResetDeferredCount(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the throttle retry count from message properties.
        /// Used for tracking exponential backoff retries due to rate limiting.
        /// </summary>
        int ThrottleRetryCount { get; }

        /// <summary>
        /// Schedules the current message for redelivery after a delay.
        /// Creates a new message with the same content and completes the original.
        /// Used for exponential backoff when Cosmos DB is throttled.
        /// </summary>
        /// <param name="delay">The delay before the message is redelivered.</param>
        /// <param name="throttleRetryCount">The retry count to set on the new message.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task ScheduleRedelivery(TimeSpan delay, int throttleRetryCount, CancellationToken cancellationToken = default);

        /// <summary>
        /// Time the inbound message spent in Service Bus before the handler was
        /// invoked (enqueued → handler entry). Set by ServiceBusAdapter at the
        /// receive boundary; read by ResponseService when constructing the
        /// outgoing response so the Resolver can persist it.
        /// </summary>
        new long? QueueTimeMs { get; set; }

        /// <summary>
        /// Time the handler spent running (handler entry → completion or failure).
        /// May be set explicitly (e.g. by middleware) or computed by
        /// ResponseService at response-build time from <see cref="HandlerStartedAtUtc"/>.
        /// </summary>
        new long? ProcessingTimeMs { get; set; }

        /// <summary>
        /// UTC timestamp captured at handler entry (immediately before the
        /// pipeline runs). ResponseService uses this to compute processing
        /// time when the outgoing response is built — needed because the
        /// terminal handler sends the response INSIDE the pipeline, before
        /// any post-await middleware (e.g. MetricsMiddleware) can finalise
        /// <see cref="ProcessingTimeMs"/>.
        /// </summary>
        DateTime? HandlerStartedAtUtc { get; set; }
    }
}