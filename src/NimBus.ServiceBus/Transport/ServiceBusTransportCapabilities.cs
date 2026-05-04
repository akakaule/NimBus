using NimBus.Transport.Abstractions;

namespace NimBus.ServiceBus.Transport;

/// <summary>
/// Capability descriptor for the Azure Service Bus transport. All four flags reflect
/// native broker features: sessions, scheduled enqueue, auto-forward, and unbounded
/// session-key cardinality.
/// </summary>
internal sealed class ServiceBusTransportCapabilities : ITransportCapabilities
{
    /// <inheritdoc />
    public bool SupportsNativeSessions => true;

    /// <inheritdoc />
    public bool SupportsScheduledEnqueue => true;

    /// <inheritdoc />
    public bool SupportsAutoForward => true;

    /// <inheritdoc />
    /// <remarks>
    /// Service Bus sessions impose no hard upper bound on the number of distinct
    /// session keys per entity, so the transport reports unbounded ordering
    /// partitions.
    /// </remarks>
    public int? MaxOrderingPartitions => null;
}
