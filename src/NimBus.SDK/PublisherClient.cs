using Azure.Messaging.ServiceBus;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.ServiceBus;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK;

public class PublisherClient : IPublisherClient
{
    private readonly ISender _sender;

    /// <summary>
    /// Creates a new PublisherClient with the specified sender.
    /// Preferred for DI registration via <see cref="Extensions.ServiceCollectionExtensions.AddNimBusPublisher"/>.
    /// </summary>
    /// <param name="sender">The sender to use for publishing messages.</param>
    public PublisherClient(ISender sender)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
    }

    /// <summary>
    /// Creates a new PublisherClient asynchronously.
    /// </summary>
    /// <param name="client">The ServiceBusClient to use for publishing messages.</param>
    /// <param name="endpoint">The endpoint (topic name) to publish messages to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A new PublisherClient instance.</returns>
    public static Task<PublisherClient> CreateAsync(
        ServiceBusClient client,
        string endpoint,
        CancellationToken cancellationToken = default)
    {
        if (client == null) throw new ArgumentNullException(nameof(client));
        if (string.IsNullOrEmpty(endpoint)) throw new ArgumentException("Endpoint cannot be null or empty.", nameof(endpoint));

        var serviceBusSender = client.CreateSender(endpoint);
        var sender = new Sender(serviceBusSender);

        return Task.FromResult(new PublisherClient(sender));
    }

    /// <summary>
    /// Creates a new PublisherClient.
    /// </summary>
    [Obsolete("Use CreateAsync instead for async initialization.")]
    public PublisherClient(ServiceBusClient client, string endpoint)
    {
        if (client == null) throw new ArgumentNullException(nameof(client));
        if (string.IsNullOrEmpty(endpoint)) throw new ArgumentException("Endpoint cannot be null or empty.", nameof(endpoint));

        var serviceBusSender = client.CreateSender(endpoint);
        _sender = new Sender(serviceBusSender);
    }

    public async Task Publish(IEvent @event)
    {
        await Publish(@event, @event.GetSessionId(), Guid.NewGuid().ToString());
    }

    public async Task Publish(IEvent @event, string sessionId, string correlationId)
    {
        var eventType = @event.GetEventType();
        var messagePayload = JsonConvert.SerializeObject(@event);
        var messageId = $"{eventType.Id}-{DeterministicHash(messagePayload)}";

        await Publish(@event, sessionId, correlationId, messageId);
    }

    public async Task Publish(IEvent @event, string sessionId, string correlationId, string messageId)
    {
        var eventType = @event.GetEventType().Id;
        using var activity = NimBusDiagnostics.Source.StartActivity("NimBus.Publish", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "servicebus");
        activity?.SetTag("messaging.destination", eventType);
        activity?.SetTag("messaging.event_type", eventType);

        var message = GetMessage(@event, correlationId, messageId, sessionId);
        activity?.SetTag("messaging.message_id", message.MessageId);
        activity?.SetTag("messaging.session_id", message.SessionId);

        try
        {
            await _sender.Send(message);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Pre-release - use with care!
    /// Publish multiple messages at once. Make sure batch size enforced by Azure Service Bus is taken into account.
    /// </summary>
    /// <param name="events">List of events you want to publish. Make sure to make them before publishing</param>
    /// <param name="correlationId"></param>
    /// <returns></returns>
    public async Task PublishBatch(IEnumerable<IEvent> events, string correlationId = null)
    {
        using var activity = NimBusDiagnostics.Source.StartActivity("NimBus.PublishBatch", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "servicebus");

        if (correlationId == null)
        {
            correlationId = Guid.NewGuid().ToString();
        }

        var messages = events.Select(@event => GetMessage(@event, correlationId)).ToList();
        activity?.SetTag("messaging.batch.message_count", messages.Count);

        try
        {
            await _sender.Send(messages);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Use to get batch of maximum possible size supported by Azure Service Bus
    /// </summary>
    /// <param name="events">Events you want to split into multiple batches</param>
    /// <returns>Batches of events</returns>
    public IEnumerable<IEnumerable<IEvent>> GetBatches(List<IEvent> events)
    {
        return GetBatchesStatic(events);
    }

    /// <summary>
    /// Use to get batch of maximum possible size supported by Azure Service Bus
    /// </summary>
    /// <param name="events">Events you want to split into multiple batches</param>
    /// <returns>Batches of events</returns>
    public static IEnumerable<IEnumerable<IEvent>> GetBatchesStatic(List<IEvent> events)
    {
        // There is a 256 KB limit per message sent on Azure Service Bus.
        // We will divide it into messages block lower or equal to 256 KB.
        // Maximum message size: 256 KB for Standard tier, 1 MB for Premium tier.
        // Maximum header size: 64 KB.
        // Our user properties are pretty significant, so we'll leave half the size for that as well.
        // https://docs.microsoft.com/en-us/azure/service-bus-messaging/service-bus-quotas
        var maxBatchSize = 256000 - 64000 - 128000;
        do
        {
            var pageSize = 0L;
            var page = events.TakeWhile(@event =>
            {
                var messageToPublish = MessageHelper.ToServiceBusMessage(GetMessageStatic(@event));
                if (pageSize + messageToPublish.Body.ToArray().Length > maxBatchSize)
                    return false;

                pageSize += messageToPublish.Body.ToArray().Length;
                return true;
            }).ToList();

            if (page.Count == 0 && events.Count > 0)
            {
                // Oversized event — yield as single-item batch, let Service Bus reject at send time
                page = new List<IEvent> { events[0] };
            }

            events.RemoveRange(0, page.Count);
            yield return page;
        } while (events.Any());
    }

    private IMessage GetMessage(IEvent @event, string correlationId = null, string messageId = null, string sessionId = null)
    {
        return GetMessageStatic(@event, correlationId, messageId, sessionId);
    }

    private static IMessage GetMessageStatic(IEvent @event, string correlationId = null, string messageId = null, string sessionId = null)
    {
        @event.Validate();

        var eventType = @event.GetEventType().Id;
        var messagePayload = JsonConvert.SerializeObject(@event);
        messageId ??= $"{eventType}-{DeterministicHash(messagePayload)}";
        sessionId ??= @event.GetSessionId();
        correlationId ??= Guid.NewGuid().ToString();
        var message = new Message()
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
                    EventJson = messagePayload
                }
            }
        };
        message.DiagnosticId = Activity.Current?.Id;
        return message;
    }

    private static string DeterministicHash(string input)
    {
        var hash = System.IO.Hashing.XxHash64.HashToUInt64(
            System.Text.Encoding.UTF8.GetBytes(input));
        return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }
}