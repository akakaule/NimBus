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
    private readonly List<(IMessage Message, int EnqueueDelay)> _sentMessagesWithDelay = new();
    private readonly ConcurrentDictionary<string, FakeServiceBusSession> _sessionsBySessionId = new();
    private readonly List<long> _scheduledSequenceNumbers = new();
    private readonly List<long> _cancelledSequenceNumbers = new();
    private readonly object _lock = new();
    private long _nextScheduledSequenceNumber;

    public IReadOnlyList<IMessage> SentMessages
    {
        get { lock (_lock) { return _allSentMessages.ToList(); } }
    }

    /// <summary>Messages with their enqueue delay (in minutes), for verifying retry backoff delays.</summary>
    public IReadOnlyList<(IMessage Message, int EnqueueDelay)> SentMessagesWithDelay
    {
        get { lock (_lock) { return _sentMessagesWithDelay.ToList(); } }
    }

    public int MessageCount => _messages.Count;

    public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _allSentMessages.Add(message);
            _sentMessagesWithDelay.Add((message, messageEnqueueDelay));
        }
        _messages.Enqueue(message);
        return Task.CompletedTask;
    }

    public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
    {
        foreach (var message in messages)
        {
            lock (_lock)
            {
                _allSentMessages.Add(message);
                _sentMessagesWithDelay.Add((message, messageEnqueueDelay));
            }
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
            var session = GetOrCreateSession(message.SessionId);
            var context = new MessageContext(sbMessage, session);

            await messageHandler.Handle(context, cancellationToken);
        }
    }

    /// <summary>
    /// Delivers all pending messages to multiple subscribers, simulating topic fan-out.
    /// </summary>
    public async Task DeliverAllToSubscribers(IEnumerable<IMessageHandler> messageHandlers, CancellationToken cancellationToken = default)
    {
        var handlers = messageHandlers?.ToList() ?? throw new ArgumentNullException(nameof(messageHandlers));
        if (handlers.Count == 0)
            throw new ArgumentException("At least one message handler must be provided.", nameof(messageHandlers));
        var sessionsBySubscriberAndSession = new Dictionary<(int subscriberIndex, string sessionId), FakeServiceBusSession>();

        while (_messages.TryDequeue(out var message))
        {
            for (int subscriberIndex = 0; subscriberIndex < handlers.Count; subscriberIndex++)
            {
                var messageHandler = handlers[subscriberIndex];
                var receivedMessage = ToReceivedMessage(message);
                var sbMessage = new NimBus.ServiceBus.ServiceBusMessage(receivedMessage);
                var sessionKey = (subscriberIndex, NormalizeSessionId(message.SessionId));
                if (!sessionsBySubscriberAndSession.TryGetValue(sessionKey, out var session))
                {
                    session = new FakeServiceBusSession();
                    sessionsBySubscriberAndSession[sessionKey] = session;
                }

                var context = new MessageContext(sbMessage, session);

                await messageHandler.Handle(context, cancellationToken);
            }
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
            var session = GetOrCreateSession(message.SessionId);
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

    /// <summary>Broker-assigned scheduled sequence numbers handed out so far, newest last.</summary>
    public IReadOnlyList<long> ScheduledSequenceNumbers
    {
        get { lock (_lock) { return _scheduledSequenceNumbers.ToList(); } }
    }

    /// <summary>Sequence numbers passed to <see cref="CancelScheduledMessage(long, CancellationToken)"/>, in order.</summary>
    public IReadOnlyList<long> CancelledSequenceNumbers
    {
        get { lock (_lock) { return _cancelledSequenceNumbers.ToList(); } }
    }

    public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
    {
        var enqueueDelay = (int)Math.Ceiling((scheduledEnqueueTime - DateTimeOffset.UtcNow).TotalMinutes);
        long sequenceNumber;
        lock (_lock)
        {
            // Service Bus assigns a POSITIVE sequence number to every scheduled
            // message; the handle bridge rejects anything else, so the fake must
            // model the real broker rather than returning a placeholder 0.
            sequenceNumber = ++_nextScheduledSequenceNumber;
            _scheduledSequenceNumbers.Add(sequenceNumber);
            _allSentMessages.Add(message);
            _sentMessagesWithDelay.Add((message, enqueueDelay));
        }
        _messages.Enqueue(message);
        return Task.FromResult(sequenceNumber);
    }

    public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _cancelledSequenceNumbers.Add(sequenceNumber);
        }
        return Task.CompletedTask;
    }

    private FakeServiceBusSession GetOrCreateSession(string? sessionId)
    {
        var normalizedSessionId = NormalizeSessionId(sessionId);
        return _sessionsBySessionId.GetOrAdd(normalizedSessionId, _ => new FakeServiceBusSession());
    }

    private static string NormalizeSessionId(string? sessionId) => sessionId ?? string.Empty;

    /// <summary>
    /// Converts an IMessage to a ServiceBusReceivedMessage using MessageHelper and ServiceBusModelFactory.
    /// Simulates Azure Service Bus SQL Rule Actions that inject EventId, From, and To.
    /// </summary>
    private static ServiceBusReceivedMessage ToReceivedMessage(IMessage message) =>
        ToReceivedMessage(MessageHelper.ToServiceBusMessage(message));

    /// <summary>
    /// Same conversion for a broker message produced OUTSIDE this bus — e.g. the one
    /// the real ManagerClient sends — so a test can deliver it through the identical
    /// wire round trip.
    /// </summary>
    internal static ServiceBusReceivedMessage ToReceivedMessage(Azure.Messaging.ServiceBus.ServiceBusMessage sbOutgoing)
    {
        ArgumentNullException.ThrowIfNull(sbOutgoing);

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
            replyTo: sbOutgoing.ReplyTo,
            replyToSessionId: sbOutgoing.ReplyToSessionId,
            properties: properties,
            enqueuedTime: DateTimeOffset.UtcNow);
    }
}

internal sealed record DeliveryResult(
    IMessage OriginalMessage,
    MessageContext Context,
    FakeServiceBusSession Session,
    Exception? Exception);
