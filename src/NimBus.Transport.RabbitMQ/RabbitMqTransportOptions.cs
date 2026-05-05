using System;

namespace NimBus.Transport.RabbitMQ;

/// <summary>
/// Configuration shape for the RabbitMQ transport provider. Consumed by
/// <c>AddRabbitMqTransport</c> via the standard <c>IOptions</c> pipeline.
/// </summary>
/// <remarks>
/// Either <see cref="Uri"/> or the discrete <see cref="HostName"/> /
/// <see cref="Port"/> / <see cref="VirtualHost"/> / <see cref="UserName"/> /
/// <see cref="Password"/> set must be supplied. When both are present,
/// <see cref="Uri"/> wins. Connection-recovery is on by default; the underlying
/// <c>RabbitMQ.Client.ConnectionFactory.AutomaticRecoveryEnabled</c> handles
/// transient broker disconnects without consumer code seeing them.
/// </remarks>
public sealed class RabbitMqTransportOptions
{
    /// <summary>
    /// AMQP URI (e.g. <c>amqp://user:pass@host:5672/vhost</c>). When supplied, the
    /// discrete <see cref="HostName"/> / <see cref="Port"/> / <see cref="VirtualHost"/> /
    /// credentials are ignored.
    /// </summary>
    public string? Uri { get; set; }

    /// <summary>
    /// Broker host name. Default: <c>localhost</c>.
    /// </summary>
    public string HostName { get; set; } = "localhost";

    /// <summary>
    /// Broker AMQP port. Default: 5672 (or 5671 when <see cref="UseTls"/> is true).
    /// </summary>
    public int Port { get; set; } = 5672;

    /// <summary>
    /// Virtual host. Default: <c>/</c>.
    /// </summary>
    public string VirtualHost { get; set; } = "/";

    /// <summary>
    /// Broker username. Default: <c>guest</c>.
    /// </summary>
    public string UserName { get; set; } = "guest";

    /// <summary>
    /// Broker password. Default: <c>guest</c>.
    /// </summary>
    public string Password { get; set; } = "guest";

    /// <summary>
    /// Number of consistent-hash partition queues per endpoint. Forward-only after
    /// provisioning — reducing partitions would re-shard live ordering keys, which
    /// the topology provisioner refuses with an explicit error. Default: 16.
    /// </summary>
    public int PartitionsPerEndpoint { get; set; } = 16;

    /// <summary>
    /// Maximum delivery attempts before a message is dead-lettered to the
    /// per-endpoint DLX. Mirrors Service Bus's default. Default: 10.
    /// </summary>
    public int MaxDeliveryCount { get; set; } = 10;

    /// <summary>
    /// Consumer prefetch count — upper bound on unacknowledged messages each
    /// consumer holds. Default: 32. Higher values trade throughput for ordering
    /// fairness across partitions.
    /// </summary>
    public ushort PrefetchCount { get; set; } = 32;

    /// <summary>
    /// Enables TLS (AMQPS). When true and <see cref="Port"/> is the default 5672,
    /// the port is automatically promoted to 5671.
    /// </summary>
    public bool UseTls { get; set; }

    /// <summary>
    /// Enables RabbitMQ.Client automatic-recovery. Default: true. Disable only
    /// for tests that want to observe disconnect failures directly.
    /// </summary>
    public bool AutomaticRecoveryEnabled { get; set; } = true;

    /// <summary>
    /// Idle interval between automatic-recovery attempts. Default: 5 seconds.
    /// </summary>
    public TimeSpan NetworkRecoveryInterval { get; set; } = TimeSpan.FromSeconds(5);
}
