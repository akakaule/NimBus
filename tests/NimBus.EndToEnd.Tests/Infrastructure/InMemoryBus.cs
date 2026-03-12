using NimBus.Core.Messages;
using NimBus.ServiceBus;
using Azure.Messaging.ServiceBus;
using System.Collections.Concurrent;

namespace NimBus.EndToEnd.Tests.Infrastructure;

/// <summary>
/// In-memory ISender that captures published messages and can deliver them
/// to a subscriber via a ServiceBusAdapter, simulating the Azure Service Bus transport.
/// </summary>
internal sealed class InMemoryBus : ISender
{
    private readonly ConcurrentQueue<IMessage> _messages = new();
    private readonly List<IMessage> _allSentMessages = new();
    private readonly object _lock = new();

    public IReadOnlyList<IMessage> SentMessages
    {
        get { lock (_lock) { return _allSentMessages.ToList(); } }
    }

    public int MessageCount => _messages.Count;

    public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
    {
        lock (_lock) { _allSentMessages.Add(message); }
        _messages.Enqueue(message);
        return Task.CompletedTask;
    }

    public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
    {
        foreach (var message in messages)
        {
            lock (_lock) { _allSentMessages.Add(message); }
            _messages.Enqueue(message);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Delivers all pending messages to the given subscriber handler, simulating
    /// the Azure Service Bus → ServiceBusAdapter → MessageHandler pipeline.
    /// </summary>
    public async Task DeliverAll(IMessageHandler messageHandler, CancellationToken cancellationToken = default)
    {
        while (_messages.TryDequeue(out var message))
        {
            var receivedMessage = ToReceivedMessage(message);
            var sbMessage = new NimBus.ServiceBus.ServiceBusMessage(receivedMessage);
            var session = new FakeServiceBusSession();
            var context = new MessageContext(sbMessage, session);

            await messageHandler.Handle(context, cancellationToken);
        }
    }

    /// <summary>
    /// Delivers all pending messages and returns per-message results with session tracking.
    /// </summary>
    public async Task<List<DeliveryResult>> DeliverAllWithResults(IMessageHandler messageHandler, CancellationToken cancellationToken = default)
    {
        var results = new List<DeliveryResult>();

        while (_messages.TryDequeue(out var message))
        {
            var receivedMessage = ToReceivedMessage(message);
            var sbMessage = new NimBus.ServiceBus.ServiceBusMessage(receivedMessage);
            var session = new FakeServiceBusSession();
            var context = new MessageContext(sbMessage, session);
            Exception? caughtException = null;

            try
            {
                await messageHandler.Handle(context, cancellationToken);
            }
            catch (Exception ex)
            {
                caughtException = ex;
            }

            results.Add(new DeliveryResult(message, context, session, caughtException));
        }

        return results;
    }

    /// <summary>
    /// Converts an IMessage to a ServiceBusReceivedMessage using MessageHelper and ServiceBusModelFactory.
    /// Simulates Azure Service Bus SQL Rule Actions that inject EventId, From, and To.
    /// </summary>
    private static ServiceBusReceivedMessage ToReceivedMessage(IMessage message)
    {
        var sbOutgoing = MessageHelper.ToServiceBusMessage(message);

        var properties = sbOutgoing.ApplicationProperties.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value);

        // Simulate SQL Rule Action: SET user.EventId = newid()
        var eventIdKey = "EventId";
        if (!properties.ContainsKey(eventIdKey) || properties[eventIdKey] is null or "")
            properties[eventIdKey] = Guid.NewGuid().ToString();

        // Simulate SQL Rule Action: SET user.From = '<topicName>'
        var fromKey = "From";
        if (!properties.ContainsKey(fromKey) || properties[fromKey] is null or "")
            properties[fromKey] = "test-topic";

        return ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: sbOutgoing.Body,
            messageId: sbOutgoing.MessageId,
            sessionId: sbOutgoing.SessionId,
            correlationId: sbOutgoing.CorrelationId,
            properties: properties,
            enqueuedTime: DateTimeOffset.UtcNow);
    }
}

internal sealed record DeliveryResult(
    IMessage OriginalMessage,
    MessageContext Context,
    FakeServiceBusSession Session,
    Exception? Exception);
