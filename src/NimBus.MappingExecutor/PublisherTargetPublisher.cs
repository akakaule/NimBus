using System;
using System.Threading;
using System.Threading.Tasks;
using NimBus.Core.Messages;
using NimBus.SDK;

namespace NimBus.MappingExecutor;

/// <summary>
/// Publishes a transformed target event onto the bus by building a classless
/// <see cref="Message"/> (mirroring <c>AgentImplementation.PostAgentPublishAsync</c>)
/// and forwarding it via <see cref="IPublisherClient.Publish(IMessage, CancellationToken)"/>
/// (spec 023).
/// </summary>
public sealed class PublisherTargetPublisher : IMappingTargetPublisher
{
    private readonly IPublisherClient _publisher;

    /// <summary>Initialises the publisher with the SDK publisher client.</summary>
    public PublisherTargetPublisher(IPublisherClient publisher)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    }

    /// <inheritdoc/>
    public Task Publish(string targetEventTypeId, string payload, string sessionId, CancellationToken ct)
    {
        // Mirror AgentImplementation.PostAgentPublishAsync message construction exactly:
        // To / EventTypeId = targetEventTypeId; SessionId from caller (non-empty guaranteed
        // by MappingExecutorHandler.SessionIdOf which falls back to new Guid); new Guid for
        // CorrelationId and MessageId; RetryCount = 0; MessageType = EventRequest;
        // MessageContent.EventContent carries the transformed payload.
        var message = new Message
        {
            To = targetEventTypeId,
            EventTypeId = targetEventTypeId,
            SessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString() : sessionId,
            CorrelationId = Guid.NewGuid().ToString(),
            MessageId = Guid.NewGuid().ToString(),
            RetryCount = 0,
            MessageType = MessageType.EventRequest,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = targetEventTypeId,
                    EventJson = payload,
                },
            },
        };

        return _publisher.Publish(message, ct);
    }
}
