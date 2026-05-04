using NimBus.Core.Messages.Exceptions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Messages
{
    /// <summary>
    /// Read-only view of a message that has been received from a transport.
    /// Promoted into <c>NimBus.Transport.Abstractions</c> alongside
    /// <see cref="IMessageContext"/> so transport adapters expose the same
    /// contract without depending on <c>NimBus.Core</c>; namespace stays
    /// <c>NimBus.Core.Messages</c> with a <c>[TypeForwardedTo]</c> in
    /// <c>NimBus.Core</c> preserving source compatibility for existing consumers.
    /// </summary>
    public interface IReceivedMessage : IMessage
    {
        DateTime EnqueuedTimeUtc { get; }
        new string MessageId { get; }

        new string EventTypeId { get; }
        new string DeadLetterReason { get; }
        new string DeadLetterErrorDescription { get; }
    }

    /// <summary>
    /// Transport-agnostic message-handling context: read-only message access plus
    /// settle operations (<c>Complete</c>, <c>Abandon</c>, <c>DeadLetter</c>,
    /// <c>Defer</c>) that providers translate into broker-native primitives.
    ///
    /// Promoted into <c>NimBus.Transport.Abstractions</c> as Pass 2 of issue #18,
    /// after task #16 removed <c>ScheduleRedelivery</c> and
    /// <c>ThrottleRetryCount</c> (now living on the Cosmos throttling hosted
    /// service / <see cref="IMessage"/> wire model respectively). Namespace is
    /// retained as <c>NimBus.Core.Messages</c> with a <c>[TypeForwardedTo]</c>
    /// declaration in the <c>NimBus.Core</c> assembly so existing
    /// <c>using NimBus.Core.Messages;</c> directives stay source-compatible.
    ///
    /// The <c>[Obsolete]</c> session-state bridges remain for one major version
    /// while consumers migrate to <see cref="NimBus.MessageStore.Abstractions.ISessionStateStore"/>
    /// injection (issue #16 follow-up D removes them; this file moves them
    /// verbatim).
    /// </summary>
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
