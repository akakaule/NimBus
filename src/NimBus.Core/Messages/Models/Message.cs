namespace NimBus.Core.Messages
{
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
        public long? QueueTimeMs { get; set; }
        public long? ProcessingTimeMs { get; set; }
        public string DeadLetterReason { get; set; }
        public string DeadLetterErrorDescription { get; set; }
    }
}
