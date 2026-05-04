namespace NimBus.Transport.Abstractions;

/// <summary>
/// Optional provider-specific capabilities consumers can branch on. Lets operator
/// tooling (topology view, conformance suite, scheduled-enqueue features) light up
/// only when the active transport supports it, and surface a clear error otherwise.
/// </summary>
public interface ITransportCapabilities
{
    /// <summary>
    /// True when the underlying transport offers native session / per-key ordered
    /// delivery (e.g. Azure Service Bus sessions). Providers that emulate ordering
    /// via partitioned queues (e.g. RabbitMQ consistent-hash exchange + single-active
    /// consumer) return false.
    /// </summary>
    bool SupportsNativeSessions { get; }

    /// <summary>
    /// True when the transport can natively schedule a message for future delivery
    /// without the platform round-tripping through the message store. Service Bus
    /// returns true (<c>ScheduledEnqueueTimeUtc</c>); RabbitMQ returns true only when
    /// the <c>rabbitmq_delayed_message_exchange</c> plugin is enabled.
    /// </summary>
    bool SupportsScheduledEnqueue { get; }

    /// <summary>
    /// True when the transport supports broker-side auto-forwarding from one
    /// endpoint to another (Service Bus <c>ForwardTo</c>). RabbitMQ returns false;
    /// equivalent topology must be assembled with bindings.
    /// </summary>
    bool SupportsAutoForward { get; }

    /// <summary>
    /// Maximum number of distinct ordering partitions per endpoint, or <c>null</c>
    /// when the transport supports an unbounded number of session keys (Service Bus).
    /// RabbitMQ providers typically return a finite value (default 16) reflecting
    /// the consistent-hash exchange partition count.
    /// </summary>
    int? MaxOrderingPartitions { get; }
}
