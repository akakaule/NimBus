namespace NimBus.Core.Messages
{
    /// <summary>
    /// Canonical wire model for every message that flows through NimBus, regardless
    /// of which transport carries it. Promoted into <c>NimBus.Transport.Abstractions</c>
    /// so provider packages (Service Bus, RabbitMQ, in-memory test transport) can
    /// implement the contract without referencing <c>NimBus.Core</c>; the namespace
    /// stays <c>NimBus.Core.Messages</c> with a <c>[TypeForwardedTo]</c> in
    /// <c>NimBus.Core</c> keeping existing <c>using NimBus.Core.Messages;</c>
    /// directives source-compatible.
    /// </summary>
    public interface IMessage
    {
        string EventId { get; }
        string To { get; }
        string SessionId { get; }
        string CorrelationId { get; }
        string MessageId { get; }
        MessageType MessageType { get; }
        MessageContent MessageContent { get; }

        /// <summary>
        /// The message this message was a response to.
        ///
        /// If this message was the first for the Event (EventRequest message type), ParentMessageId will be Constants.Self.
        /// </summary>
        /// <value></value>
        string ParentMessageId { get; }

        /// <summary>
        /// The message that initially spawned the Event -- the chronologically first message.
        ///
        /// If this message was the first for the Event (EventRequest message type), OriginatingMessageId will be Constants.Self.
        /// </summary>
        /// <value></value>
        string OriginatingMessageId { get; }
        int? RetryCount { get; }
        string From { get; }
        string OriginatingFrom { get; }

        public string EventTypeId { get; }

        /// <summary>
        /// The original session ID for deferred messages sent to the non-session subscription.
        /// </summary>
        string OriginalSessionId { get; }

        /// <summary>
        /// The sequence number for ordering deferred messages within a session.
        /// </summary>
        int? DeferralSequence { get; }

        /// <summary>
        /// W3C trace context diagnostic ID for distributed tracing.
        /// </summary>
        string DiagnosticId => null;

        /// <summary>
        /// Reply-to destination for request/response patterns.
        /// Set by the requester; the responder sends the response to this address.
        /// </summary>
        string ReplyTo => null;

        /// <summary>
        /// Session ID for reply correlation in request/response patterns.
        /// The requester listens on this session for the response.
        /// </summary>
        string ReplyToSessionId => null;

        /// <summary>
        /// Time the message spent in Service Bus before the handler was invoked
        /// (enqueued → handler entry). Populated on response messages by the
        /// receiving subscriber so the Resolver can persist it on the audit doc.
        /// Null on original publishes.
        /// </summary>
        long? QueueTimeMs => null;

        /// <summary>
        /// Time the handler took to run (handler entry → completion / failure).
        /// Populated on response messages by the receiving subscriber. Null on
        /// original publishes.
        /// </summary>
        long? ProcessingTimeMs => null;

        /// <summary>
        /// Reason the message was dead-lettered, mirrored from the Service Bus
        /// dead-letter properties. Set on dead-letter notification messages
        /// sent to the Resolver so it can record the dead-letter outcome.
        /// </summary>
        string DeadLetterReason => null;

        /// <summary>
        /// Detailed error description for a dead-lettered message (typically
        /// the formatted exception). Set on dead-letter notification messages
        /// sent to the Resolver — its presence drives the Resolver to classify
        /// the audit record as DeadLettered.
        /// </summary>
        string DeadLetterErrorDescription => null;

        /// <summary>
        /// Per-message counter for storage-throttling redelivery. Stamped by the
        /// Cosmos throttling hosted service when a 429 forces a scheduled resend;
        /// read by the Resolver on the next receive to decide whether to redeliver
        /// again or dead-letter. Zero on original publishes.
        /// </summary>
        int ThrottleRetryCount => 0;
    }
}
