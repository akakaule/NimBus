using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
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
    private readonly INimBusMessageStore _cosmosClient;
    private readonly ServiceBusClient _sbClient;
    private readonly IPlatform _platform;

    public SeedDataService(INimBusMessageStore cosmosClient, ServiceBusClient sbClient, IPlatform platform)
    {
        _cosmosClient = cosmosClient;
        _sbClient = sbClient;
        _platform = platform;
    }

    private IEnumerable<IEndpoint> PlatformEndpoints => _platform.Endpoints;

    public async Task<SeedResult> SeedAsync()
    {
        var result = new SeedResult();

        // Create endpoint metadata
        foreach (var endpoint in PlatformEndpoints)
        {
            await _cosmosClient.SetEndpointMetadata(new EndpointMetadata
            {
                EndpointId = endpoint.Id,
                EndpointOwner = "EET Integration Team",
                EndpointOwnerTeam = "Platform",
                EndpointOwnerEmail = "integration@eet.dk",
                SubscriptionStatus = true,
                IsHeartbeatEnabled = true
            });
            result.EndpointsCreated++;
        }

        // Seed events + linked messages for each endpoint
        foreach (var endpoint in PlatformEndpoints)
        {
            await SeedEndpointEvents(endpoint, result);
        }

        // Seed metrics messages (shared messages container)
        await SeedMetricsMessages(result);

        // Seed subscriptions
        await SeedSubscriptions();

        result.Message = "Sample data seeded successfully";
        return result;
    }

    private const int PendingCount = 4;
    private const int FailedCount = 5;
    private const int DeferredCount = 2;
    private const int DeadLetteredCount = 2;
    private const double SeedWindowHours = 72.0; // 3 days

    private async Task SeedEndpointEvents(IEndpoint endpoint, SeedResult result)
    {
        var endpointId = endpoint.Id;
        var now = DateTime.UtcNow;
        var pendingSessionId = $"session-{endpointId}-pending";

        // Pick event types: prefer consumed (subscriber scenarios), fall back to produced
        var eventTypes = endpoint.EventTypesConsumed.Any()
            ? endpoint.EventTypesConsumed.Select(e => e.Id).ToList()
            : endpoint.EventTypesProduced.Select(e => e.Id).ToList();

        if (eventTypes.Count == 0) return;

        // Pending events - shared session (realistic: messages waiting in queue)
        for (int i = 0; i < PendingCount; i++)
        {
            var eventTypeId = eventTypes[i % eventTypes.Count];
            var hoursAgo = SeedWindowHours / PendingCount * i;
            var timestamp = now.AddHours(-hoursAgo);
            var eventId = $"seed-pending-{endpointId}-{i}";
            var messageId = $"seed-msg-pending-{endpointId}-{i}";
            var content = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = eventTypeId,
                    EventJson = GenerateSamplePayload(eventTypeId, eventId)
                }
            };

            var evt = CreateUnresolvedEvent(eventId, pendingSessionId, endpointId,
                ResolutionStatus.Pending, eventTypeId, timestamp);
            evt.MessageContent = content;
            await _cosmosClient.UploadPendingMessage(eventId, pendingSessionId, endpointId, evt);

            await _cosmosClient.StoreMessage(CreateMessageEntity(
                eventId, messageId, endpointId, EndpointRole.Subscriber,
                MessageType.EventRequest, eventTypeId, timestamp, pendingSessionId, content));

            result.EventsCreated++;
            result.MessagesCreated++;
        }

        // Failed events - each gets its own session (realistic: independent order sessions)
        // The first failed event (i=0) gets deferred events blocked behind it
        for (int i = 0; i < FailedCount; i++)
        {
            var failedSessionId = $"session-{endpointId}-fail-{i}";
            var eventTypeId = eventTypes[i % eventTypes.Count];
            var hoursAgo = SeedWindowHours / FailedCount * i + 2;
            var timestamp = now.AddHours(-hoursAgo);
            var eventId = $"seed-failed-{endpointId}-{i}";
            var messageId = $"seed-msg-failed-{endpointId}-{i}";
            var (errorText, errorType, stackTrace) = FailureScenarios[i % FailureScenarios.Length];
            var content = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = eventTypeId,
                    EventJson = GenerateSamplePayload(eventTypeId, eventId)
                },
                ErrorContent = new ErrorContent
                {
                    ErrorText = errorText,
                    ErrorType = errorType,
                    ExceptionStackTrace = stackTrace
                }
            };

            var evt = CreateUnresolvedEvent(eventId, failedSessionId, endpointId,
                ResolutionStatus.Failed, eventTypeId, timestamp);
            evt.RetryCount = 3 + i;
            evt.RetryLimit = 5 + i;
            evt.MessageContent = content;
            await _cosmosClient.UploadFailedMessage(eventId, failedSessionId, endpointId, evt);

            await _cosmosClient.StoreMessage(CreateMessageEntity(
                eventId, messageId, endpointId, EndpointRole.Subscriber,
                MessageType.ErrorResponse, eventTypeId, timestamp, failedSessionId, content));

            result.EventsCreated++;
            result.MessagesCreated++;

            // Create deferred events blocked behind the first failed event
            if (i == 0)
            {
                for (int d = 0; d < DeferredCount; d++)
                {
                    var deferredEventTypeId = eventTypes[(d + 1) % eventTypes.Count];
                    var deferredTimestamp = timestamp.AddMinutes(d + 1);
                    var deferredEventId = $"seed-deferred-{endpointId}-{d}";
                    var deferredMessageId = $"seed-msg-deferred-{endpointId}-{d}";
                    var deferredContent = new MessageContent
                    {
                        EventContent = new EventContent
                        {
                            EventTypeId = deferredEventTypeId,
                            EventJson = GenerateSamplePayload(deferredEventTypeId, deferredEventId)
                        }
                    };

                    var deferredEvt = CreateUnresolvedEvent(deferredEventId, failedSessionId, endpointId,
                        ResolutionStatus.Deferred, deferredEventTypeId, deferredTimestamp);
                    deferredEvt.Reason = $"Blocked by {eventId}";
                    deferredEvt.MessageContent = deferredContent;
                    await _cosmosClient.UploadDeferredMessage(deferredEventId, failedSessionId, endpointId, deferredEvt);

                    await _cosmosClient.StoreMessage(CreateMessageEntity(
                        deferredEventId, deferredMessageId, endpointId, EndpointRole.Subscriber,
                        MessageType.EventRequest, deferredEventTypeId, deferredTimestamp, failedSessionId, deferredContent));

                    result.EventsCreated++;
                    result.MessagesCreated++;
                }
            }
        }

        // DeadLettered events - each gets its own session
        for (int i = 0; i < DeadLetteredCount; i++)
        {
            var dlSessionId = $"session-{endpointId}-dl-{i}";
            var eventTypeId = eventTypes[i % eventTypes.Count];
            var hoursAgo = SeedWindowHours / DeadLetteredCount * i + 10; // offset by 10h
            var timestamp = now.AddHours(-hoursAgo);
            var eventId = $"seed-deadletter-{endpointId}-{i}";
            var messageId = $"seed-msg-deadletter-{endpointId}-{i}";
            var (errorText, errorType, stackTrace) = FailureScenarios[i % FailureScenarios.Length];
            var content = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = eventTypeId,
                    EventJson = GenerateSamplePayload(eventTypeId, eventId)
                },
                ErrorContent = new ErrorContent
                {
                    ErrorText = errorText,
                    ErrorType = errorType,
                    ExceptionStackTrace = stackTrace
                }
            };

            var evt = CreateUnresolvedEvent(eventId, dlSessionId, endpointId,
                ResolutionStatus.DeadLettered, eventTypeId, timestamp);
            evt.RetryCount = 10;
            evt.RetryLimit = 10;
            evt.DeadLetterReason = "MaxDeliveryCountExceeded";
            evt.DeadLetterErrorDescription = "Max delivery count of 10 exceeded";
            evt.MessageContent = content;
            await _cosmosClient.UploadDeadletteredMessage(eventId, dlSessionId, endpointId, evt);

            await _cosmosClient.StoreMessage(CreateMessageEntity(
                eventId, messageId, endpointId, EndpointRole.Subscriber,
                MessageType.ErrorResponse, eventTypeId, timestamp, dlSessionId, content));

            result.EventsCreated++;
            result.MessagesCreated++;
        }
    }

    private async Task SeedSubscriptions()
    {
        var endpoints = PlatformEndpoints.ToList();
        if (endpoints.Count == 0) return;

        await _cosmosClient.SubscribeToEndpointNotification(
            endpointId: endpoints[0].Id,
            mail: "alerts@eet.dk",
            type: "mail",
            author: "seed-user",
            url: "",
            eventTypes: new List<string>(),
            payload: "",
            frequency: 0);

        if (endpoints.Count > 1)
        {
            await _cosmosClient.SubscribeToEndpointNotification(
                endpointId: endpoints[1].Id,
                mail: "alerts@eet.dk",
                type: "teams",
                author: "seed-user",
                url: "https://teams.webhook.example.com/dis-alerts",
                eventTypes: new List<string>(),
                payload: "",
                frequency: 0);
        }
    }

    private List<(string EndpointId, EndpointRole Role, MessageType MsgType, string EventTypeId, int Count)> BuildMetricsSpecs()
    {
        var specs = new List<(string EndpointId, EndpointRole Role, MessageType MsgType, string EventTypeId, int Count)>();
        var random = new Random(42); // fixed seed for deterministic counts (required for cleanup)

        foreach (var endpoint in PlatformEndpoints)
        {
            foreach (var eventType in endpoint.EventTypesProduced)
            {
                specs.Add((endpoint.Id, EndpointRole.Publisher, MessageType.EventRequest, eventType.Id, random.Next(8, 20)));
                specs.Add((endpoint.Id, EndpointRole.Publisher, MessageType.ErrorResponse, eventType.Id, random.Next(1, 5)));
            }

            foreach (var eventType in endpoint.EventTypesConsumed)
            {
                specs.Add((endpoint.Id, EndpointRole.Subscriber, MessageType.ResolutionResponse, eventType.Id, random.Next(6, 15)));
                specs.Add((endpoint.Id, EndpointRole.Subscriber, MessageType.ErrorResponse, eventType.Id, random.Next(1, 4)));
            }
        }

        return specs;
    }

    private async Task SeedMetricsMessages(SeedResult result)
    {
        var now = DateTime.UtcNow;
        var specs = BuildMetricsSpecs();

        // Flatten all messages so we can distribute timestamps evenly across the full window
        var allMessages = new List<(string EndpointId, EndpointRole Role, MessageType MsgType, string EventTypeId)>();
        foreach (var (endpointId, role, msgType, eventTypeId, count) in specs)
        {
            for (int i = 0; i < count; i++)
                allMessages.Add((endpointId, role, msgType, eventTypeId));
        }

        var total = allMessages.Count;
        for (int index = 0; index < total; index++)
        {
            var (endpointId, role, msgType, eventTypeId) = allMessages[index];
            var hoursAgo = SeedWindowHours / total * index; // spread evenly across 72 hours
            var timestamp = now.AddHours(-hoursAgo);
            var eventId = $"seed-metrics-{index}";
            var messageId = $"seed-metrics-msg-{index}";

            // Build MessageContent so Event Type displays and Insights can read ErrorContent
            MessageContent content;
            if (msgType == MessageType.ErrorResponse)
            {
                var (errorText, errorType, stackTrace) = FailureScenarios[index % FailureScenarios.Length];
                content = new MessageContent
                {
                    EventContent = new EventContent { EventTypeId = eventTypeId },
                    ErrorContent = new ErrorContent
                    {
                        ErrorText = errorText,
                        ErrorType = errorType,
                        ExceptionStackTrace = stackTrace
                    }
                };
            }
            else
            {
                content = new MessageContent
                {
                    EventContent = new EventContent
                    {
                        EventTypeId = eventTypeId,
                        EventJson = GenerateSamplePayload(eventTypeId, eventId)
                    }
                };
            }

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
                MessageContent = content,
            });

            result.MessagesCreated++;
        }
    }

    public async Task ClearSeedDataAsync()
    {
        foreach (var endpoint in PlatformEndpoints)
        {
            var endpointId = endpoint.Id;

            // Pending events — shared session
            var pendingSessionId = $"session-{endpointId}-pending";
            for (int i = 0; i < PendingCount; i++)
            {
                var eventId = $"seed-pending-{endpointId}-{i}";
                var messageId = $"seed-msg-pending-{endpointId}-{i}";
                try { await _cosmosClient.RemoveMessage(eventId, pendingSessionId, endpointId); } catch { }
                try { await _cosmosClient.RemoveStoredMessage(eventId, messageId); } catch { }
            }

            // Failed events — per-failure session; deferred events on first failure's session
            for (int i = 0; i < FailedCount; i++)
            {
                var failedSessionId = $"session-{endpointId}-fail-{i}";
                var eventId = $"seed-failed-{endpointId}-{i}";
                var messageId = $"seed-msg-failed-{endpointId}-{i}";
                try { await _cosmosClient.RemoveMessage(eventId, failedSessionId, endpointId); } catch { }
                try { await _cosmosClient.RemoveStoredMessage(eventId, messageId); } catch { }

                if (i == 0)
                {
                    for (int d = 0; d < DeferredCount; d++)
                    {
                        var deferredEventId = $"seed-deferred-{endpointId}-{d}";
                        var deferredMessageId = $"seed-msg-deferred-{endpointId}-{d}";
                        try { await _cosmosClient.RemoveMessage(deferredEventId, failedSessionId, endpointId); } catch { }
                        try { await _cosmosClient.RemoveStoredMessage(deferredEventId, deferredMessageId); } catch { }
                    }
                }
            }

            // Dead-lettered events — per-dl session
            for (int i = 0; i < DeadLetteredCount; i++)
            {
                var dlSessionId = $"session-{endpointId}-dl-{i}";
                var eventId = $"seed-deadletter-{endpointId}-{i}";
                var messageId = $"seed-msg-deadletter-{endpointId}-{i}";
                try { await _cosmosClient.RemoveMessage(eventId, dlSessionId, endpointId); } catch { }
                try { await _cosmosClient.RemoveStoredMessage(eventId, messageId); } catch { }
            }

            // Also clean up legacy single-session seed data (from previous seed format)
            var legacySessionId = $"session-{endpointId}-001";
            var legacyPrefixes = new[] { "seed-pending-", "seed-failed-", "seed-deferred-", "seed-deadletter-" };
            var legacyCounts = new[] { PendingCount, FailedCount, DeferredCount, DeadLetteredCount };
            for (int p = 0; p < legacyPrefixes.Length; p++)
            {
                for (int i = 0; i < legacyCounts[p]; i++)
                {
                    var eventId = $"{legacyPrefixes[p]}{endpointId}-{i}";
                    var msgPrefix = legacyPrefixes[p].Replace("seed-", "seed-msg-");
                    var messageId = $"{msgPrefix}{endpointId}-{i}";
                    try { await _cosmosClient.RemoveMessage(eventId, legacySessionId, endpointId); } catch { }
                    try { await _cosmosClient.RemoveStoredMessage(eventId, messageId); } catch { }
                }
            }
        }

        // Clean up seeded metrics messages from shared messages container
        var totalMetrics = BuildMetricsSpecs().Sum(s => s.Count);
        for (int i = 0; i < totalMetrics; i++)
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

    private static readonly (string ErrorText, string ErrorType, string StackTrace)[] FailureScenarios =
    [
        (
            "Connection timeout to downstream system",
            "System.TimeoutException",
            """
            System.TimeoutException: The operation has timed out.
               at System.Net.Http.HttpClient.SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
               at NimBus.Handlers.OrderPlacedHandler.HandleAsync(OrderPlaced evt, IMessageContext ctx) in /src/Handlers/OrderPlacedHandler.cs:line 42
               at NimBus.Core.Messages.StrictMessageHandler.InvokeAsync(IMessageContext context) in /src/NimBus.Core/Messages/StrictMessageHandler.cs:line 87
               at NimBus.ServiceBus.ServiceBusAdapter.ProcessMessageAsync(ProcessSessionMessageEventArgs args) in /src/NimBus.ServiceBus/ServiceBusAdapter.cs:line 156
            """
        ),
        (
            "Cosmos DB request rate too large (429). Retry after 1200ms.",
            "Microsoft.Azure.Cosmos.CosmosException",
            """
            Microsoft.Azure.Cosmos.CosmosException: Response status code does not indicate success: TooManyRequests (429); Request rate is large.
               at Microsoft.Azure.Cosmos.ResponseMessage.EnsureSuccessStatusCode()
               at Microsoft.Azure.Cosmos.CosmosClient.ExecuteAsync(RequestMessage request, CancellationToken cancellationToken)
               at NimBus.MessageStore.CosmosDbClient.UpsertItemAsync[T](Container container, T item, PartitionKey pk) in /src/NimBus.MessageStore/CosmosDbClient.cs:line 314
               at NimBus.Handlers.PaymentCapturedHandler.HandleAsync(PaymentCaptured evt, IMessageContext ctx) in /src/Handlers/PaymentCapturedHandler.cs:line 58
               at NimBus.Core.Messages.StrictMessageHandler.InvokeAsync(IMessageContext context) in /src/NimBus.Core/Messages/StrictMessageHandler.cs:line 87
            """
        ),
    ];

    private static string GenerateSamplePayload(string eventTypeId, string eventId)
    {
        var id = Guid.NewGuid();
        return eventTypeId switch
        {
            "CustomerRegistered" => $@"{{""customerId"":""{id}"",""email"":""customer-{eventId[^4..]}@example.com"",""fullName"":""Jane Doe"",""segment"":""Enterprise""}}",
            "OrderPlaced" => $@"{{""orderId"":""{id}"",""customerId"":""{Guid.NewGuid()}"",""currencyCode"":""EUR"",""totalAmount"":249.95,""salesChannel"":""Web""}}",
            "PaymentCaptured" => $@"{{""orderId"":""{id}"",""paymentId"":""{Guid.NewGuid()}"",""amount"":249.95,""capturedAt"":""{DateTime.UtcNow:O}""}}",
            "InventoryReserved" => $@"{{""orderId"":""{id}"",""reservationId"":""{Guid.NewGuid()}"",""warehouseCode"":""WH-DK01"",""reservedLines"":3}}",
            "ShipmentDispatched" => $@"{{""orderId"":""{id}"",""shipmentId"":""{Guid.NewGuid()}"",""carrier"":""PostNord"",""trackingNumber"":""PN{id.ToString()[..8].ToUpperInvariant()}"",""dispatchedAt"":""{DateTime.UtcNow:O}""}}",
            "CustomerNotified" => $@"{{""orderId"":""{id}"",""notificationId"":""{Guid.NewGuid()}"",""template"":""OrderConfirmation"",""channel"":""Email"",""sentAt"":""{DateTime.UtcNow:O}""}}",
            _ => $@"{{""id"":""{id}"",""eventType"":""{eventTypeId}"",""timestamp"":""{DateTime.UtcNow:O}""}}"
        };
    }

    private static MessageEntity CreateMessageEntity(string eventId, string messageId,
        string endpointId, EndpointRole role, MessageType msgType, string eventTypeId,
        DateTime timestamp, string sessionId, MessageContent content)
    {
        return new MessageEntity
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
            SessionId = sessionId,
            CorrelationId = Guid.NewGuid().ToString(),
            OriginatingMessageId = $"orig-{eventId}",
            MessageContent = content,
        };
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
