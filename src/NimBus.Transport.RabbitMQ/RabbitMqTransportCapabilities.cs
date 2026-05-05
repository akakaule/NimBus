using Microsoft.Extensions.Options;
using NimBus.Transport.Abstractions;

namespace NimBus.Transport.RabbitMQ;

/// <summary>
/// Capability descriptor for the RabbitMQ transport. Sessions are emulated via
/// the <c>rabbitmq_consistent_hash_exchange</c> + <c>single-active-consumer</c>
/// pattern (so <see cref="SupportsNativeSessions"/> is false but per-key ordering
/// is still preserved within <see cref="MaxOrderingPartitions"/>). Scheduled
/// enqueue is available when the <c>rabbitmq_delayed_message_exchange</c> plugin
/// is loaded; the transport refuses to start without it.
/// </summary>
public sealed class RabbitMqTransportCapabilities : ITransportCapabilities
{
    private readonly IOptions<RabbitMqTransportOptions> _options;

    public RabbitMqTransportCapabilities(IOptions<RabbitMqTransportOptions> options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public bool SupportsNativeSessions => false;

    /// <inheritdoc />
    public bool SupportsScheduledEnqueue => true;

    /// <inheritdoc />
    public bool SupportsAutoForward => false;

    /// <inheritdoc />
    public int? MaxOrderingPartitions => _options.Value.PartitionsPerEndpoint;
}
