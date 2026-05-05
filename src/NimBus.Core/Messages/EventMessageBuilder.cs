using System;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Newtonsoft.Json;
using NimBus.Core.Events;

namespace NimBus.Core.Messages;

/// <summary>
/// Shared helper that builds an outbound <see cref="IMessage"/> from an
/// <see cref="IEvent"/>. Single source of truth for the message-id hash and
/// envelope shape — used by <c>PublisherClient</c> and by transport-specific
/// request/response senders so the on-wire shape stays identical.
/// </summary>
public static class EventMessageBuilder
{
    /// <summary>
    /// Builds an <see cref="IMessage"/> for <paramref name="event"/>. Defaults
    /// match <c>PublisherClient.GetMessageStatic</c> exactly: deterministic
    /// message id keyed off the event-type and JSON payload, session id taken
    /// from the event's <c>[SessionKey]</c>, fresh correlation id when none
    /// supplied.
    /// </summary>
    public static Message Build(IEvent @event, string? correlationId = null, string? messageId = null, string? sessionId = null)
    {
        if (@event is null) throw new ArgumentNullException(nameof(@event));
        @event.Validate();

        var eventType = @event.GetEventType().Id;
        var messagePayload = JsonConvert.SerializeObject(@event);
        messageId ??= $"{eventType}-{DeterministicHash(messagePayload)}";
        sessionId ??= @event.GetSessionId();
        correlationId ??= Guid.NewGuid().ToString();

        return new Message
        {
            To = eventType,
            EventTypeId = eventType,
            SessionId = sessionId,
            CorrelationId = correlationId,
            MessageId = messageId,
            RetryCount = 0,
            MessageType = MessageType.EventRequest,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = eventType,
                    EventJson = messagePayload,
                },
            },
            DiagnosticId = Activity.Current?.Id,
        };
    }

    private static string DeterministicHash(string input)
    {
        var hash = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(input));
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }
}
