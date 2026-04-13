namespace NimBus.Core.Messages
{
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
    }

    public class Message : IMessage
    {
        public string To { get; set; }

        public string SessionId { get; set; }

        public MessageType MessageType { get; set; }

        public MessageContent MessageContent { get; set; }

        public string EventId { get; set; }

        public string CorrelationId { get; set; }

        public string MessageId { get; set; }

        public string ParentMessageId { get; set; }

        public string OriginatingMessageId { get; set; }
        public string From { get; set; }
        public string OriginatingFrom { get; set; }
        public int? RetryCount { get; set; }
        public string EventTypeId { get; set; }
        public string OriginalSessionId { get; set; }
        public int? DeferralSequence { get; set; }
        public string DiagnosticId { get; set; }
        public string ReplyTo { get; set; }
        public string ReplyToSessionId { get; set; }
    }
}
