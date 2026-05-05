using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;
using RabbitMQ.Client;
using NimBusConstants = NimBus.Core.Messages.Constants;

namespace NimBus.Transport.RabbitMQ;

/// <summary>
/// Wire-format helper that translates between <see cref="IMessage"/> and the
/// AMQP <see cref="BasicProperties"/> + body pair RabbitMQ.Client uses. Mirrors
/// <c>MessageHelper.ToServiceBusMessage</c> in <c>NimBus.ServiceBus</c> so an
/// audit trail produced under one transport is observably identical under the
/// other.
/// </summary>
internal static class RabbitMqMessageHelper
{
    /// <summary>
    /// AMQP header carrying the consistent-hash routing-key value (the session
    /// key). The <c>x-consistent-hash</c> exchange routes by this header when
    /// the binding has its hash-on argument set to the same name.
    /// </summary>
    public const string SessionKeyHeader = "session-key";

    /// <summary>
    /// AMQP header used by the <c>rabbitmq_delayed_message_exchange</c> plugin
    /// to schedule a message for future delivery (value: milliseconds).
    /// </summary>
    public const string DelayHeader = "x-delay";

    public static (BasicProperties properties, ReadOnlyMemory<byte> body) BuildMessage(
        IMessage message,
        long? delayMilliseconds = null)
    {
        var headers = new Dictionary<string, object?>
        {
            [UserPropertyName.To.ToString()] = message.To,
            [UserPropertyName.MessageType.ToString()] = message.MessageType.ToString(),
            [UserPropertyName.EventId.ToString()] = message.EventId,
            [UserPropertyName.OriginatingMessageId.ToString()] = message.OriginatingMessageId ?? NimBusConstants.Self,
            [UserPropertyName.ParentMessageId.ToString()] = message.ParentMessageId ?? NimBusConstants.Self,
            [UserPropertyName.RetryCount.ToString()] = message.RetryCount ?? 0,
            [UserPropertyName.OriginatingFrom.ToString()] = message.OriginatingFrom ?? NimBusConstants.Self,
            [UserPropertyName.EventTypeId.ToString()] = message.EventTypeId ?? message.MessageContent?.EventContent?.EventTypeId,
        };

        if (!string.IsNullOrEmpty(message.From))
            headers[UserPropertyName.From.ToString()] = message.From;
        if (!string.IsNullOrEmpty(message.OriginalSessionId))
            headers[UserPropertyName.OriginalSessionId.ToString()] = message.OriginalSessionId;
        if (message.DeferralSequence.HasValue)
            headers[UserPropertyName.DeferralSequence.ToString()] = message.DeferralSequence.Value;
        if (message.QueueTimeMs.HasValue)
            headers[UserPropertyName.QueueTimeMs.ToString()] = message.QueueTimeMs.Value;
        if (message.ProcessingTimeMs.HasValue)
            headers[UserPropertyName.ProcessingTimeMs.ToString()] = message.ProcessingTimeMs.Value;
        if (!string.IsNullOrEmpty(message.DeadLetterReason))
            headers[UserPropertyName.DeadLetterReason.ToString()] = message.DeadLetterReason;
        if (!string.IsNullOrEmpty(message.DeadLetterErrorDescription))
            headers[UserPropertyName.DeadLetterErrorDescription.ToString()] = message.DeadLetterErrorDescription;
        if (message.ThrottleRetryCount > 0)
            headers[UserPropertyName.ThrottleRetryCount.ToString()] = message.ThrottleRetryCount;

        var diagnosticId = message.DiagnosticId ?? Activity.Current?.Id;
        if (!string.IsNullOrEmpty(diagnosticId))
            headers[NimBusDiagnostics.DiagnosticIdProperty] = diagnosticId;

        if (!string.IsNullOrEmpty(message.SessionId))
            headers[SessionKeyHeader] = message.SessionId;

        if (delayMilliseconds is { } delay && delay > 0)
            headers[DelayHeader] = delay;

        var properties = new BasicProperties
        {
            Headers = headers,
            DeliveryMode = DeliveryModes.Persistent,
            ContentType = "application/json",
            ContentEncoding = "utf-8",
        };

        if (!string.IsNullOrWhiteSpace(message.MessageId))
            properties.MessageId = message.MessageId;
        if (!string.IsNullOrEmpty(message.CorrelationId))
            properties.CorrelationId = message.CorrelationId;
        if (!string.IsNullOrEmpty(message.ReplyTo))
            properties.ReplyTo = message.ReplyTo;

        var serialized = JsonConvert.SerializeObject(message.MessageContent);
        var body = Encoding.UTF8.GetBytes(serialized);

        return (properties, body);
    }
}
