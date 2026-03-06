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
        public string OriginatingFrom { get; set; }
        public int? RetryCount { get; set; }
        public string EventTypeId { get; set; }
        public string OriginalSessionId { get; set; }
        public int? DeferralSequence { get; set; }
    }
}
