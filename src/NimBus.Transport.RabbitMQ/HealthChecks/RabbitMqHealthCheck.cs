using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NimBus.Transport.RabbitMQ.Connection;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace NimBus.Transport.RabbitMQ.HealthChecks;

/// <summary>
/// Health check for the RabbitMQ transport. Verifies (a) connection liveness,
/// (b) the <c>rabbitmq_consistent_hash_exchange</c> plugin is loaded, and
/// (c) the <c>rabbitmq_delayed_message_exchange</c> plugin is loaded. Both
/// plugins are hard prerequisites; missing either fails the check with a
/// remediation hint pointing at <c>rabbitmq-plugins enable</c>.
/// </summary>
public sealed class RabbitMqHealthCheck : IHealthCheck
{
    private readonly RabbitMqConnectionFactory _connectionFactory;

    public RabbitMqHealthCheck(RabbitMqConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var connection = await _connectionFactory.GetOrCreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            if (!connection.IsOpen)
            {
                return HealthCheckResult.Unhealthy("RabbitMQ connection is not open.");
            }

            await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            var consistentHashOk = await ProbeExchangeTypeAsync(connection, "x-consistent-hash", cancellationToken).ConfigureAwait(false);
            if (!consistentHashOk)
            {
                return HealthCheckResult.Unhealthy(
                    "rabbitmq_consistent_hash_exchange plugin is not loaded. Run " +
                    "'rabbitmq-plugins enable rabbitmq_consistent_hash_exchange' on the broker.");
            }

            var delayedOk = await ProbeExchangeTypeAsync(connection, "x-delayed-message", cancellationToken).ConfigureAwait(false);
            if (!delayedOk)
            {
                return HealthCheckResult.Unhealthy(
                    "rabbitmq_delayed_message_exchange plugin is not loaded. Run " +
                    "'rabbitmq-plugins enable rabbitmq_delayed_message_exchange' on the broker.");
            }

            return HealthCheckResult.Healthy(
                $"RabbitMQ connection active; both consistent-hash and delayed-message plugins loaded. Endpoint: {connection.Endpoint}");
        }
        catch (OperationInterruptedException ex)
        {
            return HealthCheckResult.Unhealthy("RabbitMQ broker rejected the health probe.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("RabbitMQ health probe failed.", ex);
        }
    }

    private static async Task<bool> ProbeExchangeTypeAsync(IConnection connection, string exchangeType, CancellationToken cancellationToken)
    {
        // We probe by attempting a passive exchange-declare against a unique
        // throwaway name on a fresh channel. If the plugin is loaded the
        // exchange type is recognised and the broker responds with
        // exchange-not-found (ack-shaped from a passive declare); if missing,
        // the broker rejects the type with a connection-level error. Either
        // way the channel is disposed when done.
        var probeExchange = $"nimbus-health-{exchangeType}-{Guid.NewGuid():N}";
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            await channel.ExchangeDeclareAsync(
                exchange: probeExchange,
                type: exchangeType,
                durable: false,
                autoDelete: true,
                arguments: exchangeType == "x-delayed-message"
                    ? new Dictionary<string, object?> { ["x-delayed-type"] = "direct" }
                    : null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await channel.ExchangeDeleteAsync(probeExchange, ifUnused: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationInterruptedException)
        {
            return false;
        }
    }
}
