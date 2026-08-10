using Amqp;
using Amqp.Framing;
using Amqp.Types;
using NimBus.ServiceBusEmulator.Broker;

namespace NimBus.ServiceBusEmulator.Protocol;

internal static class AmqpMessageConverter
{
    public const uint ServiceBusBatchFormat = 0x80013700;
    private static readonly Symbol SequenceNumber = new("x-opt-sequence-number");
    private static readonly Symbol EnqueuedTime = new("x-opt-enqueued-time");
    private static readonly Symbol LockedUntil = new("x-opt-locked-until");
    private static readonly Symbol ScheduledEnqueueTime = new("x-opt-scheduled-enqueue-time");
    private static readonly Symbol PartitionKey = new("x-opt-partition-key");
    private static readonly Symbol ViaPartitionKey = new("x-opt-via-partition-key");

    public static BrokerMessage FromAmqp(Message source)
    {
        var properties = source.Properties;
        var annotations = source.MessageAnnotations;
        var applicationProperties = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (source.ApplicationProperties is not null)
        {
            foreach (var pair in source.ApplicationProperties.Map)
            {
                applicationProperties[(string)pair.Key] = pair.Value;
            }
        }

        return new BrokerMessage
        {
            Body = source.BodySection switch
            {
                Data data => data.Binary,
                AmqpValue { Value: byte[] bytes } => bytes,
                null => ReadOnlyMemory<byte>.Empty,
                _ => throw new NotSupportedException("Spec 027 supports binary Service Bus message bodies only."),
            },
            // Real Service Bus assigns a broker-side id when the sender omits message-id;
            // NimBus's ResponseService relies on that for responses to MessageId-less
            // native messages, so mirror it at ingest.
            MessageId = properties?.GetMessageId()?.ToString() is { Length: > 0 } messageId
                ? messageId
                : Guid.NewGuid().ToString(),
            CorrelationId = properties?.GetCorrelationId()?.ToString(),
            SessionId = properties?.GroupId,
            ReplyToSessionId = properties?.ReplyToGroupId,
            ReplyTo = properties?.ReplyTo,
            ContentType = properties?.ContentType,
            Subject = properties?.Subject,
            To = properties?.To,
            PartitionKey = GetAnnotation<string>(annotations, PartitionKey),
            TransactionPartitionKey = GetAnnotation<string>(annotations, ViaPartitionKey),
            TimeToLive = source.Header is { Ttl: > 0 } header ? TimeSpan.FromMilliseconds(header.Ttl) : null,
            ScheduledEnqueueTime = GetTimestamp(annotations, ScheduledEnqueueTime),
            ApplicationProperties = applicationProperties,
        };
    }

    public static IReadOnlyList<BrokerMessage> FromTransfer(Message source)
    {
        if (source.Format != ServiceBusBatchFormat)
        {
            return [FromAmqp(source)];
        }

        var sections = source.Body switch
        {
            Data[] data => data,
            _ => throw new FormatException("A Service Bus batch must contain one Data section per encoded message."),
        };
        return sections.Select(section =>
        {
            var bytes = section.Binary;
            return FromAmqp(Message.Decode(new ByteBuffer(bytes, 0, bytes.Length, bytes.Length)));
        }).ToArray();
    }

    public static Message ToAmqp(BrokerMessage source)
    {
        var message = new Message
        {
            Header = new Header
            {
                Durable = true,
                DeliveryCount = (uint)Math.Max(0, source.DeliveryCount - 1),
                Ttl = source.TimeToLive is { } ttl ? checked((uint)Math.Clamp(ttl.TotalMilliseconds, 0, uint.MaxValue)) : 0,
            },
            Properties = new Properties
            {
                GroupId = source.SessionId,
                ReplyToGroupId = source.ReplyToSessionId,
                ReplyTo = source.ReplyTo,
                ContentType = source.ContentType,
                Subject = source.Subject,
                To = source.To,
            },
            MessageAnnotations = new MessageAnnotations(),
            ApplicationProperties = new ApplicationProperties(),
            BodySection = new Data { Binary = source.Body.ToArray() },
        };
        message.Properties.SetMessageId(source.MessageId);
        message.Properties.SetCorrelationId(source.CorrelationId);
        message.MessageAnnotations[SequenceNumber] = source.SequenceNumber;
        message.MessageAnnotations[EnqueuedTime] = source.EnqueuedTime.UtcDateTime;
        message.MessageAnnotations[LockedUntil] = source.LockedUntil.UtcDateTime;
        if (source.PartitionKey is not null)
        {
            message.MessageAnnotations[PartitionKey] = source.PartitionKey;
        }

        if (source.TransactionPartitionKey is not null)
        {
            message.MessageAnnotations[ViaPartitionKey] = source.TransactionPartitionKey;
        }

        foreach (var pair in source.ApplicationProperties)
        {
            message.ApplicationProperties[pair.Key] = pair.Value;
        }

        return message;
    }

    private static T? GetAnnotation<T>(MessageAnnotations? annotations, Symbol key) where T : class =>
        annotations?.Map.TryGetValue(key, out var value) == true ? value as T : null;

    private static DateTimeOffset? GetTimestamp(MessageAnnotations? annotations, Symbol key)
    {
        if (annotations?.Map.TryGetValue(key, out var value) != true)
        {
            return null;
        }

        return value switch
        {
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            _ => null,
        };
    }
}
