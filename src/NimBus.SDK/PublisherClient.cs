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
    private ServiceBusClient _serviceBusClient;

    /// <summary>
    /// Creates a new PublisherClient with the specified sender.
    /// Preferred for DI registration via <see cref="Extensions.ServiceCollectionExtensions.AddNimBusPublisher(Microsoft.Extensions.DependencyInjection.IServiceCollection, string)"/>.
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
        var eventType = @event.GetEventType();
        var messagePayload = JsonConvert.SerializeObject(@event);
        var messageId = $"{eventType.Id}-{DeterministicHash(messagePayload)}";

        await Publish(@event, sessionId, correlationId, messageId);
    }

    public async Task Publish(IEvent @event, string sessionId, string correlationId, string messageId)
    {
        // The publisher span is emitted by the InstrumentingSenderDecorator that
        // wraps ISender (registered via AddNimBusInstrumentation). PublisherClient
        // no longer opens its own activity — the decorator's span carries the
        // canonical messaging.* attributes and parents to Activity.Current.
        var message = GetMessage(@event, correlationId, messageId, sessionId);
        await _sender.Send(message);
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
        // Span emission happens in InstrumentingSenderDecorator (see Publish above).
        if (correlationId == null)
        {
            correlationId = Guid.NewGuid().ToString();
        }

        var messages = events.Select(@event => GetMessage(@event, correlationId)).ToList();
        await _sender.Send(messages);
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
    /// Sends a request and awaits a typed response with timeout.
    /// Uses Azure Service Bus sessions for reply correlation.
    /// Requires a PublisherClient created with a ServiceBusClient (via CreateAsync or constructor).
    /// </summary>
    public async Task<TResponse> Request<TRequest, TResponse>(TRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
        where TRequest : IEvent
        where TResponse : class
    {
        if (_serviceBusClient == null)
            throw new InvalidOperationException(
                "Request/response requires a ServiceBusClient. Use PublisherClient.CreateAsync(client, endpoint) or the ServiceBusClient constructor.");

        var replySessionId = Guid.NewGuid().ToString();
        var msg = (Message)GetMessage(request);
        msg.ReplyTo = msg.To;
        msg.ReplyToSessionId = replySessionId;
        var message = (IMessage)msg;

        await _sender.Send(message, cancellationToken: cancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        ServiceBusSessionReceiver receiver = null;
        try
        {
            receiver = await _serviceBusClient.AcceptSessionAsync(
                message.To, $"{message.To}-reply", replySessionId, cancellationToken: cts.Token);

            var reply = await receiver.ReceiveMessageAsync(timeout, cts.Token);
            if (reply == null)
                throw new TimeoutException($"No response received within {timeout}");

            await receiver.CompleteMessageAsync(reply, cts.Token);

            var body = reply.Body.ToString();
            return Newtonsoft.Json.JsonConvert.DeserializeObject<TResponse>(body);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No response received within {timeout}");
        }
        finally
        {
            if (receiver != null)
                await receiver.DisposeAsync();
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
        // Trace context is captured by MessageHelper.ToServiceBusMessage at send
        // time via W3CMessagePropagator (traceparent / tracestate). The legacy
        // IMessage.DiagnosticId property is no longer populated or read.
        return message;
    }

    private static string DeterministicHash(string input)
    {
        var hash = System.IO.Hashing.XxHash64.HashToUInt64(
            System.Text.Encoding.UTF8.GetBytes(input));
        return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }
}