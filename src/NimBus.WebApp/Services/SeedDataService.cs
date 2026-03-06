using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.States;
using NimBus.ServiceBus;
using NimBus.WebApp.ManagementApi;
using ErrorContent = NimBus.Core.Messages.ErrorContent;
using EventContent = NimBus.Core.Messages.EventContent;
using MessageContent = NimBus.Core.Messages.MessageContent;
using MessageType = NimBus.Core.Messages.MessageType;
using ResolutionStatus = NimBus.MessageStore.ResolutionStatus;

namespace NimBus.WebApp.Services;

public class SeedDataService
{
    private readonly ICosmosDbClient _cosmosClient;
    private readonly ServiceBusClient _sbClient;
    private static readonly string[] SeedEndpoints = ["nav-publisher", "crm-subscriber", "sm-adapter"];

    public SeedDataService(ICosmosDbClient cosmosClient, ServiceBusClient sbClient)
    {
        _cosmosClient = cosmosClient;
        _sbClient = sbClient;
    }

    public async Task<SeedResult> SeedAsync()
    {
        var result = new SeedResult();

        // Create endpoint metadata
        foreach (var endpointId in SeedEndpoints)
        {
            await _cosmosClient.SetEndpointMetadata(new EndpointMetadata
            {
                EndpointId = endpointId,
                EndpointOwner = "EET Integration Team",
                EndpointOwnerTeam = "Platform",
                EndpointOwnerEmail = "integration@eet.dk",
                SubscriptionStatus = true,
                IsHeartbeatEnabled = true
            });
            result.EndpointsCreated++;
        }

        // Seed events for each endpoint
        foreach (var endpointId in SeedEndpoints)
        {
            await SeedEndpointEvents(endpointId, result);
        }

        // Seed metrics messages (shared messages container)
        await SeedMetricsMessages(result);

        // Seed subscriptions
        await SeedSubscriptions();

        result.Message = "Sample data seeded successfully";
        return result;
    }

    private async Task SeedEndpointEvents(string endpointId, SeedResult result)
    {
        var baseTime = DateTime.UtcNow;
        var sessionId = $"session-{endpointId}-001";

        // Pending events
        for (int i = 0; i < 2; i++)
        {
            var eventId = $"seed-pending-{endpointId}-{i}";
            var evt = CreateUnresolvedEvent(eventId, sessionId, endpointId,
                ResolutionStatus.Pending, "CustomerChanged", baseTime.AddMinutes(-30 + i));
            await _cosmosClient.UploadPendingMessage(eventId, sessionId, endpointId, evt);
            result.EventsCreated++;
        }

        // Failed events
        for (int i = 0; i < 2; i++)
        {
            var eventId = $"seed-failed-{endpointId}-{i}";
            var evt = CreateUnresolvedEvent(eventId, sessionId, endpointId,
                ResolutionStatus.Failed, "OrderCreated", baseTime.AddMinutes(-60 + i));
            evt.RetryCount = 3;
            evt.RetryLimit = 5;
            evt.MessageContent = new MessageContent
            {
                ErrorContent = new ErrorContent
                {
                    ErrorText = "Connection timeout to downstream system",
                    ErrorType = "TransientException"
                }
            };
            await _cosmosClient.UploadFailedMessage(eventId, sessionId, endpointId, evt);
            result.EventsCreated++;
        }

        // Deferred event
        {
            var eventId = $"seed-deferred-{endpointId}-0";
            var evt = CreateUnresolvedEvent(eventId, sessionId, endpointId,
                ResolutionStatus.Deferred, "CustomerChanged", baseTime.AddMinutes(-15));
            evt.Reason = $"Blocked by seed-pending-{endpointId}-0";
            await _cosmosClient.UploadDeferredMessage(eventId, sessionId, endpointId, evt);
            result.EventsCreated++;
        }

        // DeadLettered event
        {
            var eventId = $"seed-deadletter-{endpointId}-0";
            var evt = CreateUnresolvedEvent(eventId, sessionId, endpointId,
                ResolutionStatus.DeadLettered, "OrderCreated", baseTime.AddHours(-2));
            evt.RetryCount = 10;
            evt.RetryLimit = 10;
            evt.DeadLetterReason = "MaxDeliveryCountExceeded";
            evt.DeadLetterErrorDescription = "Max delivery count of 10 exceeded";
            await _cosmosClient.UploadDeadletteredMessage(eventId, sessionId, endpointId, evt);
            result.EventsCreated++;
        }
    }

    private async Task SeedSubscriptions()
    {
        await _cosmosClient.SubscribeToEndpointNotification(
            endpointId: "nav-publisher",
            mail: "alerts@eet.dk",
            type: "mail",
            author: "seed-user",
            url: "",
            eventTypes: new List<string>(),
            payload: "",
            frequency: 0);

        await _cosmosClient.SubscribeToEndpointNotification(
            endpointId: "crm-subscriber",
            mail: "alerts@eet.dk",
            type: "teams",
            author: "seed-user",
            url: "https://teams.webhook.example.com/dis-alerts",
            eventTypes: new List<string>(),
            payload: "",
            frequency: 0);
    }

    private static readonly (string EndpointId, EndpointRole Role, MessageType MsgType, string EventTypeId, int Count)[] MetricsMessageSpecs =
    [
        ("nav-publisher", EndpointRole.Publisher, MessageType.EventRequest, "CustomerChanged", 15),
        ("nav-publisher", EndpointRole.Publisher, MessageType.EventRequest, "OrderCreated", 10),
        ("crm-subscriber", EndpointRole.Subscriber, MessageType.ResolutionResponse, "CustomerChanged", 12),
        ("crm-subscriber", EndpointRole.Subscriber, MessageType.ResolutionResponse, "OrderCreated", 8),
        ("sm-adapter", EndpointRole.Subscriber, MessageType.ResolutionResponse, "CustomerChanged", 6),
        ("nav-publisher", EndpointRole.Publisher, MessageType.ErrorResponse, "OrderCreated", 4),
        ("crm-subscriber", EndpointRole.Subscriber, MessageType.ErrorResponse, "CustomerChanged", 3),
    ];

    private static int TotalMetricsMessages => 58; // sum of counts above

    private async Task SeedMetricsMessages(SeedResult result)
    {
        var now = DateTime.UtcNow;
        var index = 0;

        foreach (var (endpointId, role, msgType, eventTypeId, count) in MetricsMessageSpecs)
        {
            for (int i = 0; i < count; i++)
            {
                var hoursAgo = (20.0 / count) * i; // spread evenly across 20 hours (within 1d dashboard view)
                var timestamp = now.AddHours(-hoursAgo);
                var eventId = $"seed-metrics-{index}";
                var messageId = $"seed-metrics-msg-{index}";

                await _cosmosClient.StoreMessage(new MessageEntity
                {
                    EventId = eventId,
                    MessageId = messageId,
                    EndpointId = endpointId,
                    EndpointRole = role,
                    MessageType = msgType,
                    EventTypeId = eventTypeId,
                    EnqueuedTimeUtc = timestamp,
                    From = endpointId,
                    To = role == EndpointRole.Publisher ? "service-bus" : endpointId,
                    SessionId = $"session-{endpointId}-metrics",
                    CorrelationId = Guid.NewGuid().ToString(),
                    OriginatingMessageId = $"orig-{eventId}",
                });

                index++;
                result.MessagesCreated++;
            }
        }
    }

    public async Task ClearSeedDataAsync()
    {
        // Clean up per-endpoint events
        foreach (var endpointId in SeedEndpoints)
        {
            var sessionId = $"session-{endpointId}-001";
            var prefixes = new[] { "seed-pending-", "seed-failed-", "seed-deferred-", "seed-deadletter-" };
            foreach (var prefix in prefixes)
            {
                for (int i = 0; i < 3; i++)
                {
                    var eventId = $"{prefix}{endpointId}-{i}";
                    try
                    {
                        await _cosmosClient.RemoveMessage(eventId, sessionId, endpointId);
                    }
                    catch
                    {
                        // Event may not exist, ignore
                    }
                }
            }
        }

        // Clean up seeded metrics messages from shared messages container
        for (int i = 0; i < TotalMetricsMessages; i++)
        {
            try
            {
                await _cosmosClient.RemoveStoredMessage($"seed-metrics-{i}", $"seed-metrics-msg-{i}");
            }
            catch
            {
                // Message may not exist, ignore
            }
        }
    }

    public async Task CreateEventAsync(string endpointId, string eventTypeId, string sessionId, string status, string? messageContent)
    {
        var eventId = $"dev-{Guid.NewGuid():N}";
        sessionId ??= $"session-{endpointId}-dev";

        if (!Enum.TryParse<ResolutionStatus>(status, out var resolutionStatus))
            resolutionStatus = ResolutionStatus.Pending;

        var evt = CreateUnresolvedEvent(eventId, sessionId, endpointId, resolutionStatus, eventTypeId, DateTime.UtcNow);
        if (!string.IsNullOrEmpty(messageContent))
        {
            evt.MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = eventTypeId,
                    EventJson = messageContent
                }
            };
        }

        switch (resolutionStatus)
        {
            case ResolutionStatus.Pending:
                await _cosmosClient.UploadPendingMessage(eventId, sessionId, endpointId, evt);
                break;
            case ResolutionStatus.Failed:
                evt.RetryCount = 1;
                evt.RetryLimit = 5;
                await _cosmosClient.UploadFailedMessage(eventId, sessionId, endpointId, evt);
                break;
            case ResolutionStatus.Deferred:
                await _cosmosClient.UploadDeferredMessage(eventId, sessionId, endpointId, evt);
                break;
            case ResolutionStatus.DeadLettered:
                evt.DeadLetterReason = "ManuallyCreated";
                await _cosmosClient.UploadDeadletteredMessage(eventId, sessionId, endpointId, evt);
                break;
            case ResolutionStatus.Unsupported:
                await _cosmosClient.UploadUnsupportedMessage(eventId, sessionId, endpointId, evt);
                break;
            default:
                await _cosmosClient.UploadPendingMessage(eventId, sessionId, endpointId, evt);
                break;
        }
    }

    public async Task CreateMessageAsync(string endpointId,
        string eventTypeId, string sessionId, string messageContent)
    {
        var eventId = $"dev-{Guid.NewGuid():N}";
        var messageId = $"dev-msg-{Guid.NewGuid():N}";
        var resolvedEventTypeId = string.IsNullOrWhiteSpace(eventTypeId) ? "CustomerChanged" : eventTypeId;
        sessionId = string.IsNullOrEmpty(sessionId) ? $"session-{endpointId}-dev" : sessionId;
        MessageContent payload = null;
        if (!string.IsNullOrEmpty(messageContent))
        {
            payload = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = resolvedEventTypeId,
                    EventJson = messageContent
                }
            };
        }

        // Send the message to Azure Service Bus so downstream subscribers process it
        var correlationId = Guid.NewGuid().ToString();
        var message = new Core.Messages.Message
        {
            To = resolvedEventTypeId,
            SessionId = sessionId,
            MessageType = MessageType.EventRequest,
            EventId = eventId,
            EventTypeId = resolvedEventTypeId,
            MessageId = messageId,
            CorrelationId = correlationId,
            OriginatingMessageId = Core.Messages.Constants.Self,
            ParentMessageId = Core.Messages.Constants.Self,
            OriginatingFrom = endpointId,
            RetryCount = 0,
            MessageContent = payload,
        };

        var sbMessage = MessageHelper.ToServiceBusMessage(message);
        ServiceBusSender sender = _sbClient.CreateSender(endpointId);
        try
        {
            await sender.SendMessageAsync(sbMessage);
        }
        finally
        {
            await sender.DisposeAsync();
        }
    }

    private static UnresolvedEvent CreateUnresolvedEvent(string eventId, string sessionId,
        string endpointId, ResolutionStatus status, string eventTypeId, DateTime timestamp)
    {
        return new UnresolvedEvent
        {
            EventId = eventId,
            SessionId = sessionId,
            EndpointId = endpointId,
            ResolutionStatus = status,
            EventTypeId = eventTypeId,
            UpdatedAt = timestamp,
            EnqueuedTimeUtc = timestamp,
            From = endpointId,
            To = "resolver",
            CorrelationId = Guid.NewGuid().ToString(),
            OriginatingMessageId = $"orig-{eventId}",
            LastMessageId = $"last-{eventId}",
            EndpointRole = EndpointRole.Subscriber,
            MessageType = MessageType.EventRequest
        };
    }
}
