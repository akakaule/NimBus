using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NimBus.Transport.Abstractions;

namespace NimBus.Transport.RabbitMQ.Topology;

/// <summary>
/// RabbitMQ implementation of <see cref="ITransportSessionOps"/>.
/// </summary>
/// <remarks>
/// <para>
/// The Service Bus shape of these operations leans heavily on session-receiver
/// primitives (<c>AcceptSession</c>, <c>ReceiveDeferredMessage</c>,
/// <c>SetSessionState</c>) that have no native AMQP equivalent. The RabbitMQ
/// equivalents land alongside slice 1D (<c>RabbitMqReceiverHostedService</c>)
/// of issue #14 — they need to coordinate with the partition-queue consumer
/// and the parked-message store, which the receiver loop owns. Until that
/// lands, this implementation throws fail-loud so operator UIs surface a
/// clear "not yet supported on this transport" error rather than silently
/// no-oping.
/// </para>
/// </remarks>
public sealed class RabbitMqSessionOps : ITransportSessionOps
{
    private const string Message =
        "Session-management operations are not yet supported on the RabbitMQ transport. " +
        "The implementation depends on RabbitMqReceiverHostedService (issue #14, slice 1D) " +
        "to coordinate broker-side draining with the parked-message-store replay path. " +
        "Use --Transport servicebus for these operator workflows in the meantime.";

    public Task<TransportSessionPreview> PreviewSessionAsync(string endpointName, string sessionId, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Message);

    public Task<TransportSessionPurgeResult> PurgeSessionAsync(string endpointName, string sessionId, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Message);

    public Task<TransportSubscriptionPreview> PreviewSubscriptionAsync(
        string endpointName,
        string subscriptionName,
        IReadOnlyCollection<TransportMessageState> states,
        DateTime? enqueuedBeforeUtc,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(Message);

    public Task<TransportBulkResult> PurgeSubscriptionAsync(
        string endpointName,
        string subscriptionName,
        IReadOnlyCollection<TransportMessageState> states,
        DateTime? enqueuedBeforeUtc,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(Message);

    public Task<TransportReprocessResult> ReprocessDeferredAsync(string endpointName, string sessionId, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Message);
}
