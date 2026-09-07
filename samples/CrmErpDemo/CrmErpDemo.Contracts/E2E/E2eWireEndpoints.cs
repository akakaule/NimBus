using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Newtonsoft.Json;
using NimBus.Core.Messages;
using NimBus.SDK;
using NimBus.ServiceBus;

namespace CrmErpDemo.Contracts.E2E;

/// <summary>Bounded wire-format and settlement probes, protected by the E2E route filter.</summary>
public static class E2eWireEndpoints
{
    /// <summary>Maps probes for the fixed pair of demo endpoints, never an arbitrary destination.</summary>
    public static void MapE2eWireControls(this RouteGroupBuilder group, string ownEndpoint)
    {
        var destination = ownEndpoint == "CrmEndpoint" ? "ErpEndpoint" : "CrmEndpoint";
        var knownType = ownEndpoint == "CrmEndpoint" ? "CrmAccountUpdated" : "ErpCustomerUpdated";
        group.MapPost("/replay/{session:guid}", async (Guid session, ReplayRequest replay, ServiceBusClient bus, CancellationToken ct) =>
        {
            if (session == Guid.Empty || !Guid.TryParse(replay.EventId, out _) ||
                replay.MessageId is not { Length: > 0 and <= 200 } || replay.EventJson is not { Length: > 0 and <= 16384 })
                return Results.BadRequest();
            var eventType = ownEndpoint == "CrmEndpoint" ? "ErpCustomerUpdated" : "CrmAccountUpdated";
            var message = new Message
            {
                To = ownEndpoint, From = destination, OriginatingFrom = destination,
                EventId = replay.EventId, MessageId = replay.MessageId, CorrelationId = replay.MessageId,
                OriginatingMessageId = replay.MessageId, SessionId = session.ToString(),
                EventTypeId = eventType, MessageType = MessageType.EventRequest,
                MessageContent = new MessageContent { EventContent = new EventContent { EventTypeId = eventType, EventJson = replay.EventJson } },
            };
            await using var sender = bus.CreateSender(ownEndpoint);
            await sender.SendMessageAsync(MessageHelper.ToServiceBusMessage(message), ct);
            return Results.NoContent();
        });
        group.MapPost("/wire/{session:guid}/{kind}", async (Guid session, string kind, ServiceBusClient bus, CancellationToken ct) =>
        {
            if (kind is not ("missing-payload" or "null-payload" or "invalid-json" or "deep-json" or "mismatched-type" or "missing-event-type" or "missing-event-id" or "unsupported"))
                return Results.BadRequest();
            var eventId = Guid.NewGuid().ToString();
            var messageId = Guid.NewGuid().ToString();
            var eventType = kind == "unsupported" ? "E2eUnregisteredEvent" : knownType;
            var payload = kind switch
            {
                "null-payload" => "null",
                "invalid-json" => "{",
                "deep-json" => string.Concat(Enumerable.Repeat("{\"child\":", 40)) + "{}" + new string('}', 40),
                _ => "{}",
            };
            var message = new Message
            {
                To = destination, From = ownEndpoint, OriginatingFrom = ownEndpoint,
                EventId = eventId, MessageId = messageId, CorrelationId = messageId,
                SessionId = session.ToString(), EventTypeId = eventType, MessageType = MessageType.EventRequest,
                MessageContent = new MessageContent
                {
                    EventContent = new EventContent { EventTypeId = kind == "mismatched-type" ? "OtherType" : eventType, EventJson = payload },
                },
            };
            var wire = MessageHelper.ToServiceBusMessage(message);
            if (kind == "missing-payload") wire.Body = new BinaryData("{}");
            if (kind == "missing-event-id") wire.ApplicationProperties.Remove("EventId");
            if (kind == "missing-event-type")
            {
                wire.ApplicationProperties.Remove("EventTypeId");
                message.MessageContent.EventContent.EventTypeId = string.Empty;
                wire.Body = new BinaryData(JsonConvert.SerializeObject(message.MessageContent));
            }
            await using var sender = bus.CreateSender(destination);
            await sender.SendMessageAsync(wire, ct);
            return Results.Ok(new { eventId, messageId, endpoint = destination, eventType });
        });
        group.MapGet("/deadletters/{session:guid}", async (Guid session, ServiceBusClient bus, CancellationToken ct) =>
        {
            await using var receiver = bus.CreateReceiver(ownEndpoint, ownEndpoint, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
            var matches = new List<object>();
            long sequence = 0;
            for (var page = 0; page < 20; page++)
            {
                var messages = await receiver.PeekMessagesAsync(100, sequence, ct);
                foreach (var message in messages.Where(message => message.SessionId == session.ToString()))
                    matches.Add(new { message.MessageId, message.DeadLetterReason, message.DeadLetterErrorDescription });
                if (messages.Count < 100) break;
                sequence = messages[^1].SequenceNumber + 1;
            }
            return Results.Ok(matches);
        });
        group.MapPost("/settle/{outcome}", async (string outcome, HandoffSettlement coordinates, IHandoffClient handoff, CancellationToken ct) =>
        {
            if (outcome is not ("complete" or "fail") || !Guid.TryParse(coordinates.SessionId, out _)) return Results.BadRequest();
            if (outcome == "complete") await handoff.CompleteAsync(coordinates, new { source = "E2E" }, ct);
            else await handoff.FailAsync(coordinates, "E2E duplicate or stale failure", "E2E", ct);
            return Results.NoContent();
        });
    }

    /// <summary>Original identity and bounded business payload for a duplicate-delivery probe.</summary>
    public sealed record ReplayRequest(string EventId, string MessageId, string EventJson);
}
