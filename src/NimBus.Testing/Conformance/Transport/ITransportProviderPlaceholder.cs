namespace NimBus.Testing.Conformance.Transport;

// TODO: Replace with NimBus.Transport.Abstractions.ITransportProvider once task #2 (issue #17) lands.
// Tracked alongside task #6 (issue #21) and task #17 — this marker exists only so the
// conformance skeletons compile against an unambiguous transport-shaped placeholder.
/// <summary>
/// Placeholder marker for the not-yet-defined transport-provider abstraction.
/// </summary>
public interface ITransportProviderPlaceholder
{
}

// TODO: Replace with NimBus.Transport.Abstractions.ITransportCapabilities once task #2 (issue #17) lands.
/// <summary>
/// Placeholder marker for the not-yet-defined transport capability flags.
/// Concrete transports describe which features they support (scheduled enqueue,
/// dead-letter projection, transport-level deferral, etc.); the conformance suite
/// uses these flags to gate optional tests.
/// </summary>
public interface ITransportCapabilitiesPlaceholder
{
    /// <summary>
    /// True if the transport supports native scheduled enqueue (delayed delivery).
    /// </summary>
    bool SupportsScheduledEnqueue { get; }
}
