using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using NimBus.Transport.Abstractions;
using NimBus.Transport.RabbitMQ.Connection;
using RabbitMQ.Client;

namespace NimBus.Transport.RabbitMQ.Topology;

/// <summary>
/// RabbitMQ implementation of <see cref="ITransportManagement"/>. Each NimBus
/// endpoint is realised as:
/// <list type="bullet">
/// <item><description>An <c>x-consistent-hash</c> exchange named after the endpoint (the publish target).</description></item>
/// <item><description><c>PartitionsPerEndpoint</c> queues bound to that exchange with weight <c>"1"</c>, each marked <c>single-active-consumer = true</c>.</description></item>
/// <item><description>An <c>x-delayed-message</c> companion exchange (type <c>direct</c>) for scheduled enqueue.</description></item>
/// <item><description>A per-endpoint dead-letter exchange + dead-letter queue.</description></item>
/// </list>
/// Calls are idempotent: re-declaring an existing entity is a no-op.
/// </summary>
internal sealed class RabbitMqTransportManagement : ITransportManagement
{
    private readonly RabbitMqConnectionFactory _connectionFactory;
    private readonly RabbitMqTransportOptions _options;

    public RabbitMqTransportManagement(
        RabbitMqConnectionFactory connectionFactory,
        IOptions<RabbitMqTransportOptions> options)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task DeclareEndpointAsync(EndpointConfig config, CancellationToken cancellationToken)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));

        var connection = await _connectionFactory.GetOrCreateConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        var endpoint = config.Name;
        var dlx = RabbitMqTopologyConventions.DeadLetterExchange(endpoint);
        var dlq = RabbitMqTopologyConventions.DeadLetterQueue(endpoint);
        var delayed = RabbitMqTopologyConventions.DelayedExchange(endpoint);
        var endpointExchange = RabbitMqTopologyConventions.EndpointExchange(endpoint);

        await channel.ExchangeDeclareAsync(
            exchange: dlx,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueDeclareAsync(
            queue: dlq,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueBindAsync(
            queue: dlq,
            exchange: dlx,
            routingKey: string.Empty,
            arguments: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.ExchangeDeclareAsync(
            exchange: endpointExchange,
            type: "x-consistent-hash",
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.ExchangeDeclareAsync(
            exchange: delayed,
            type: "x-delayed-message",
            durable: true,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-delayed-type"] = "direct" },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var partitions = _options.PartitionsPerEndpoint;
        for (var i = 0; i < partitions; i++)
        {
            var queue = RabbitMqTopologyConventions.PartitionQueue(endpoint, i);
            await channel.QueueDeclareAsync(
                queue: queue,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: new Dictionary<string, object?>
                {
                    ["x-single-active-consumer"] = true,
                    ["x-dead-letter-exchange"] = dlx,
                    ["x-delivery-limit"] = _options.MaxDeliveryCount,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // Weight "1" on the consistent-hash binding distributes partitions evenly.
            await channel.QueueBindAsync(
                queue: queue,
                exchange: endpointExchange,
                routingKey: "1",
                arguments: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // Delayed-exchange routes via direct routing key matching the partition queue name.
            await channel.QueueBindAsync(
                queue: queue,
                exchange: delayed,
                routingKey: queue,
                arguments: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<EndpointInfo>> ListEndpointsAsync(CancellationToken cancellationToken)
    {
        // RabbitMQ exposes endpoint enumeration through the Management HTTP API
        // rather than AMQP. The HTTP-API client lands alongside the WebApp
        // topology view (#28) so this call is a deliberate not-yet rather than
        // an unreachable code path.
        throw new NotSupportedException(
            "Listing endpoints is not yet supported by the RabbitMQ transport-management adapter. " +
            "It will be wired through the RabbitMQ Management HTTP API alongside the transport-aware " +
            "topology view (issue #28). For now, drive the broker directly via 'rabbitmqctl list_queues' " +
            "or the management UI.");
    }

    /// <inheritdoc />
    public async Task PurgeEndpointAsync(string endpointName, CancellationToken cancellationToken)
    {
        if (endpointName is null) throw new ArgumentNullException(nameof(endpointName));

        var connection = await _connectionFactory.GetOrCreateConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < _options.PartitionsPerEndpoint; i++)
        {
            var queue = RabbitMqTopologyConventions.PartitionQueue(endpointName, i);
            await channel.QueuePurgeAsync(queue, cancellationToken).ConfigureAwait(false);
        }

        await channel.QueuePurgeAsync(
            RabbitMqTopologyConventions.DeadLetterQueue(endpointName),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveEndpointAsync(string endpointName, CancellationToken cancellationToken)
    {
        if (endpointName is null) throw new ArgumentNullException(nameof(endpointName));

        var connection = await _connectionFactory.GetOrCreateConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < _options.PartitionsPerEndpoint; i++)
        {
            await channel.QueueDeleteAsync(
                RabbitMqTopologyConventions.PartitionQueue(endpointName, i),
                ifUnused: false,
                ifEmpty: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        await channel.QueueDeleteAsync(
            RabbitMqTopologyConventions.DeadLetterQueue(endpointName),
            ifUnused: false,
            ifEmpty: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.ExchangeDeleteAsync(
            RabbitMqTopologyConventions.EndpointExchange(endpointName),
            ifUnused: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.ExchangeDeleteAsync(
            RabbitMqTopologyConventions.DelayedExchange(endpointName),
            ifUnused: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.ExchangeDeleteAsync(
            RabbitMqTopologyConventions.DeadLetterExchange(endpointName),
            ifUnused: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
