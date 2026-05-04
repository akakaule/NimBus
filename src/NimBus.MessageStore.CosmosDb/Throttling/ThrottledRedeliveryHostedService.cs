using System;
using System.Threading;
using System.Threading.Tasks;
using NimBus.Core.Messages;

namespace NimBus.MessageStore.CosmosDb.Throttling;

/// <summary>
/// Cosmos-throttling redelivery service. Replaces the SB-specific
/// <c>IMessageContext.ScheduleRedelivery</c> path that lived on the transport
/// interface. The Resolver invokes this service when storage signals a 429
/// (<see cref="Abstractions.StorageProviderTransientException"/>) — the service
/// builds an outgoing copy of the inbound message with the throttle-retry
/// counter incremented, delegates to <see cref="ISender.ScheduleMessage"/> for
/// transport-neutral scheduled delivery, and completes the original on success.
/// Lives in the Cosmos provider package because the throttling concern is
/// storage-driven; the transport never owned it.
///
/// The <c>HostedService</c> suffix matches the spec naming
/// (<c>docs/specs/003-rabbitmq-transport/spec.md</c> §"Open questions"
/// "ScheduleRedelivery Cosmos-throttling consumer"). The service is invoked
/// directly from the Resolver's catch block — it isn't run as a periodic
/// <see cref="Microsoft.Extensions.Hosting.IHostedService"/>.
/// </summary>
public sealed class ThrottledRedeliveryHostedService
{
    private readonly ISender _sender;

    public ThrottledRedeliveryHostedService(ISender sender)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
    }

    /// <summary>
    /// Schedules a copy of the inbound message for redelivery after
    /// <paramref name="delay"/>, stamped with
    /// <paramref name="throttleRetryCount"/>. Completes the original message
    /// only after the scheduled send succeeds — a failure leaves the original
    /// to retry via the transport's lock-expiration redelivery so the message
    /// is never lost.
    /// </summary>
    public async Task ScheduleRedelivery(IMessageContext messageContext, TimeSpan delay, int throttleRetryCount, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageContext);

        var copy = new Message
        {
            // New MessageId so the broker treats it as a distinct send; the
            // EventId / CorrelationId chain stays intact so the Resolver
            // continues to thread audits onto the same logical event.
            MessageId = Guid.NewGuid().ToString(),
            EventId = messageContext.EventId,
            CorrelationId = messageContext.CorrelationId,
            SessionId = messageContext.SessionId,
            To = messageContext.To,
            From = messageContext.From,
            OriginatingFrom = messageContext.OriginatingFrom,
            ParentMessageId = messageContext.ParentMessageId,
            OriginatingMessageId = messageContext.OriginatingMessageId,
            MessageType = messageContext.MessageType,
            MessageContent = messageContext.MessageContent,
            EventTypeId = messageContext.EventTypeId,
            OriginalSessionId = messageContext.OriginalSessionId,
            DeferralSequence = messageContext.DeferralSequence,
            RetryCount = messageContext.RetryCount,
            ReplyTo = messageContext.ReplyTo,
            ReplyToSessionId = messageContext.ReplyToSessionId,
            ThrottleRetryCount = throttleRetryCount,
        };

        var scheduledTime = DateTimeOffset.UtcNow.Add(delay);
        await _sender.ScheduleMessage(copy, scheduledTime, cancellationToken).ConfigureAwait(false);
        // Complete original only after the scheduled send has been accepted.
        await messageContext.Complete(cancellationToken).ConfigureAwait(false);
    }
}
