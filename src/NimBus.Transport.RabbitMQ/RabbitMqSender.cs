using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NimBus.Core.Messages;
using NimBus.Core.Outbox;
using NimBus.Transport.RabbitMQ.Connection;
using NimBus.Transport.RabbitMQ.Topology;
using RabbitMQ.Client;

namespace NimBus.Transport.RabbitMQ;

/// <summary>
/// AMQP-side <see cref="ISender"/> bound to a specific endpoint exchange.
/// Publishes to the endpoint's <c>x-consistent-hash</c> exchange (immediate
/// delivery) or its companion <c>x-delayed-message</c> exchange (scheduled
/// delivery). One channel per <see cref="RabbitMqSender"/> instance, opened
/// lazily on first publish; channels are cheap and safe to keep open for the
/// lifetime of the sender.
/// </summary>
public sealed class RabbitMqSender : ISender, INimBusDispatcherSender, IAsyncDisposable
{
    private readonly RabbitMqConnectionFactory _connectionFactory;
    private readonly string _endpointExchange;
    private readonly string _delayedExchange;
    private readonly SemaphoreSlim _channelGate = new(1, 1);
    private IChannel? _channel;

    public RabbitMqSender(RabbitMqConnectionFactory connectionFactory, string endpointName)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        if (string.IsNullOrWhiteSpace(endpointName)) throw new ArgumentException("Endpoint required.", nameof(endpointName));

        _endpointExchange = RabbitMqTopologyConventions.EndpointExchange(endpointName);
        _delayedExchange = RabbitMqTopologyConventions.DelayedExchange(endpointName);
    }

    /// <inheritdoc />
    public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default) =>
        Send(new[] { message }, messageEnqueueDelay, cancellationToken);

    /// <inheritdoc />
    public async Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
    {
        if (messages is null) throw new ArgumentNullException(nameof(messages));

        var channel = await GetChannelAsync(cancellationToken).ConfigureAwait(false);
        var delayMs = messageEnqueueDelay > 0 ? (long?)TimeSpan.FromMinutes(messageEnqueueDelay).TotalMilliseconds : null;
        var exchange = delayMs is null ? _endpointExchange : _delayedExchange;

        foreach (var message in messages)
        {
            var (properties, body) = RabbitMqMessageHelper.BuildMessage(message, delayMs);

            // The consistent-hash exchange uses the routing key for hashing; the
            // delayed exchange routes by direct match on the partition queue
            // name. Both code paths supply the session id (or a fallback) so
            // unkeyed messages are still distributed across partitions rather
            // than crowding one queue.
            var routingKey = !string.IsNullOrEmpty(message.SessionId)
                ? message.SessionId
                : message.MessageId ?? Guid.NewGuid().ToString();

            await channel.BasicPublishAsync(
                exchange: exchange,
                routingKey: routingKey,
                mandatory: false,
                basicProperties: properties,
                body: body,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// RabbitMQ's <c>rabbitmq_delayed_message_exchange</c> plugin schedules by
    /// elapsed delay rather than absolute time; the implementation converts the
    /// requested target to a delay-from-now in milliseconds. Returns 0 because
    /// AMQP does not expose a server-assigned scheduling sequence number;
    /// <see cref="CancelScheduledMessage"/> consequently throws.
    /// </remarks>
    public async Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));

        var delay = scheduledEnqueueTime - DateTimeOffset.UtcNow;
        var delayMs = (long)Math.Max(0, delay.TotalMilliseconds);
        var (properties, body) = RabbitMqMessageHelper.BuildMessage(message, delayMs);

        var channel = await GetChannelAsync(cancellationToken).ConfigureAwait(false);
        var routingKey = !string.IsNullOrEmpty(message.SessionId)
            ? message.SessionId
            : message.MessageId ?? Guid.NewGuid().ToString();

        await channel.BasicPublishAsync(
            exchange: _delayedExchange,
            routingKey: routingKey,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return 0;
    }

    /// <inheritdoc />
    public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default)
    {
        // The delayed-message-exchange plugin holds messages internally and does
        // not expose a cancel handle. Cancellation can only be modelled through
        // the outbox's pre-delivery skip flag (operator drives it from
        // nimbus-ops). Throwing fail-loud here is intentional — silent no-op
        // would mask scheduled work that should not fire.
        throw new NotSupportedException(
            "RabbitMQ does not support cancelling a scheduled message in flight. " +
            "Mark the corresponding outbox row as skipped before the scheduled " +
            "enqueue time, or rely on the message-store skip flag at receive time.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.CloseAsync().ConfigureAwait(false);
            await _channel.DisposeAsync().ConfigureAwait(false);
        }
        _channelGate.Dispose();
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true }) return _channel;

        await _channelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_channel is { IsOpen: true }) return _channel;

            var connection = await _connectionFactory.GetOrCreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            _channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return _channel;
        }
        finally
        {
            _channelGate.Release();
        }
    }
}
