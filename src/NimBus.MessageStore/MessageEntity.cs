using NimBus.Core.Messages;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;

namespace NimBus.MessageStore;

public class MessageEntity : IReceivedMessage
{
    public string EventId { get; set; }

    public string MessageId { get; set; }

    public string EventTypeId { get; set; }

    public string OriginatingMessageId { get; set; }

    public string ParentMessageId { get; set; }

    public string From { get; set; }

    public string To { get; set; }

    public string OriginatingFrom { get; set; }

    public string SessionId { get; set; }

    public string CorrelationId { get; set; }

    public DateTime EnqueuedTimeUtc { get; set; }

    public MessageContent MessageContent { get; set; }

    [JsonConverter(typeof(StringEnumConverter))]
    public MessageType MessageType { get; set; }

    [JsonConverter(typeof(StringEnumConverter))]
    public EndpointRole EndpointRole { get; set; }

    public string EndpointId { get; set; }
    public int? RetryCount { get; set; }
    public int? RetryLimit { get; set; }
    public string DeadLetterReason { get; set; }
    public string DeadLetterErrorDescription { get; set; }
    public string OriginalSessionId { get; set; }
    public int? DeferralSequence { get; set; }
    public long? QueueTimeMs { get; set; }
    public long? ProcessingTimeMs { get; set; }
}