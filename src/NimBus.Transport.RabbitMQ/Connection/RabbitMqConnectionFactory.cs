using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace NimBus.Transport.RabbitMQ.Connection;

/// <summary>
/// Builds an <see cref="IConnection"/> from the configured
/// <see cref="RabbitMqTransportOptions"/>. Held as a singleton in DI so all
/// senders, consumers, and the topology provisioner share one TCP connection
/// to the broker. Each consumer / publisher creates its own <see cref="IChannel"/>
/// off this connection — channels are cheap, connections are not.
/// </summary>
public sealed class RabbitMqConnectionFactory
{
    private readonly RabbitMqTransportOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public RabbitMqConnectionFactory(IOptions<RabbitMqTransportOptions> options)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Returns the shared connection, creating it on first call. Subsequent calls
    /// return the same instance until it is disposed by host shutdown.
    /// </summary>
    public async Task<IConnection> GetOrCreateConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is { IsOpen: true }) return _connection;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is { IsOpen: true }) return _connection;

            var factory = BuildFactory(_options);
            _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static ConnectionFactory BuildFactory(RabbitMqTransportOptions options)
    {
        var factory = new ConnectionFactory
        {
            AutomaticRecoveryEnabled = options.AutomaticRecoveryEnabled,
            NetworkRecoveryInterval = options.NetworkRecoveryInterval,
        };

        if (!string.IsNullOrWhiteSpace(options.Uri))
        {
            factory.Uri = new Uri(options.Uri);
        }
        else
        {
            factory.HostName = options.HostName;
            factory.Port = options.UseTls && options.Port == 5672 ? 5671 : options.Port;
            factory.VirtualHost = options.VirtualHost;
            factory.UserName = options.UserName;
            factory.Password = options.Password;
            if (options.UseTls)
            {
                factory.Ssl = new SslOption
                {
                    Enabled = true,
                    ServerName = options.HostName,
                };
            }
        }

        return factory;
    }
}
