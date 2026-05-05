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
    private readonly IRequestSender? _requestSender;
    private ServiceBusClient _serviceBusClient;

    /// <summary>
    /// Creates a new PublisherClient with the specified sender.
    /// Preferred for DI registration via <see cref="Extensions.ServiceCollectionExtensions.AddNimBusPublisher(Microsoft.Extensions.DependencyInjection.IServiceCollection, string)"/>.
    /// </summary>
    /// <param name="sender">The sender to use for publishing messages.</param>
    public PublisherClient(ISender sender)
        : this(sender, requestSender: null)
    {
    }

    /// <summary>
    /// Creates a new PublisherClient with the specified sender and an optional
    /// transport-supplied <see cref="IRequestSender"/> for request/response
    /// flows. The DI registration in
    /// <see cref="Extensions.ServiceCollectionExtensions.AddNimBusPublisher(Microsoft.Extensions.DependencyInjection.IServiceCollection, string)"/>
    /// resolves the request-sender from the active transport — for example,
    /// <c>AddServiceBusTransport</c> registers
    /// <see cref="ServiceBusRequestSender"/>.
    /// </summary>
    public PublisherClient(ISender sender, IRequestSender? requestSender)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _requestSender = requestSender;
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

        return Task.FromResult(new PublisherClient(sender) { _serviceBusClient = client });
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
        _serviceBusClient = client;
    }

    public async Task Publish(IEvent @event)
    {
        await Publish(@event, @event.GetSessionId(), Guid.NewGuid().ToString());
    }

    public async Task Publish(IEvent @event, string sessionId, string correlationId)
    {
        // EventMessageBuilder.Build derives the deterministic message id when
        // none is supplied; use that as the single source of truth.
        var built = EventMessageBuilder.Build(@event, correlationId, messageId: null, sessionId);
        await Publish(@event, sessionId, correlationId, built.MessageId);
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
    /// Schedules an event for delivery at the specified time.
    /// Returns a sequence number that can be used to cancel the scheduled message.
    /// </summary>
    public async Task<long> Schedule(IEvent @event, DateTimeOffset scheduledEnqueueTime)
    {
        var message = GetMessage(@event);
        return await _sender.ScheduleMessage(message, scheduledEnqueueTime);
    }

    /// <summary>
    /// Cancels a previously scheduled message using the sequence number returned by <see cref="Schedule"/>.
    /// </summary>
    public async Task CancelScheduled(long sequenceNumber)
    {
        await _sender.CancelScheduledMessage(sequenceNumber);
    }

    /// <summary>
    /// Sends a request and awaits a typed response with timeout. Delegates to
    /// the registered <see cref="IRequestSender"/> (Service Bus implementation
    /// uses session-based reply queues; other transports plug their own
    /// correlation strategy).
    /// </summary>
    public Task<TResponse> Request<TRequest, TResponse>(TRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
        where TRequest : IEvent
        where TResponse : class
    {
        if (_requestSender is null)
            throw new InvalidOperationException(
                "Request/response requires an IRequestSender. Register a transport that ships one " +
                "(e.g. AddServiceBusTransport) before resolving IPublisherClient.");

        return _requestSender.Request<TRequest, TResponse>(request, timeout, cancellationToken);
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

    private static IMessage GetMessageStatic(IEvent @event, string correlationId = null, string messageId = null, string sessionId = null)
        => EventMessageBuilder.Build(@event, correlationId, messageId, sessionId);

    private IMessage GetMessage(IEvent @event, string correlationId = null, string messageId = null, string sessionId = null)
        => EventMessageBuilder.Build(@event, correlationId, messageId, sessionId);
}