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
    /// The session-state bridge methods (BlockSession, IsSessionBlocked*,
    /// GetBlockedByEventId, deferred-count helpers) were removed in
    /// issue #16 follow-up D. Consumers inject
    /// <see cref="NimBus.MessageStore.Abstractions.ISessionStateStore"/> via DI
    /// directly. Counted post-drop: 11 members (8 transport ops including
    /// <see cref="IsDeferred"/>, plus the 3 timing properties below) — under
    /// the SC-010 ceiling of 12.
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
