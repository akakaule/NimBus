using NimBus.Transport.Abstractions;

namespace NimBus.Testing;

/// <summary>
/// Capability descriptor for the in-memory test transport. The in-memory bus emulates
/// the surface area NimBus relies on so unit tests can exercise the same code paths as
/// production transports without booting a broker.
/// </summary>
public sealed class InMemoryTransportCapabilities : ITransportCapabilities
{
    /// <summary>
    /// True. <see cref="InMemoryMessageBus"/> emulates session-keyed FIFO ordering by
    /// dispatching per <c>SessionId</c> on top of an in-process queue.
    /// </summary>
    public bool SupportsNativeSessions => true;

    /// <summary>
    /// True. <see cref="InMemoryMessageBus.ScheduleMessage"/> records and replays
    /// scheduled messages in test scenarios.
    /// </summary>
    public bool SupportsScheduledEnqueue => true;

    /// <summary>
    /// False. The in-memory bus does not model broker-side auto-forwarding chains
    /// (Service Bus <c>ForwardTo</c>); tests requiring that behaviour belong in the
    /// Service Bus suite.
    /// </summary>
    public bool SupportsAutoForward => false;

    /// <summary>
    /// <c>null</c>. Session keys are stored in an unbounded <c>ConcurrentDictionary</c>;
    /// no partition cap is imposed.
    /// </summary>
    public int? MaxOrderingPartitions => null;
}
