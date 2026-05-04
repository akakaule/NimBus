using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.Testing.Conformance.Transport;

/// <summary>
/// Provider-agnostic conformance for basic send / receive semantics: round-tripping a
/// message envelope, preserving identifying headers, and delivering messages in the
/// order a single producer sent them.
/// </summary>
/// <remarks>
/// Concrete provider runs subclass this type and override <see cref="CreateTransport"/>.
/// Test bodies are intentionally empty until the transport abstractions land
/// (task #2 / issue #17). Each method name is normative — the scenario it asserts is
/// described in its XML doc comment.
/// </remarks>
[TestClass]
public abstract class SendReceiveConformanceTests
{
    // TODO: Once task #2 (issue #17) lands, change return type to ITransportProvider.
    /// <summary>
    /// Returns a transport provider (or test-double) wired to an isolated topology.
    /// Each test invokes this once; concrete implementations must guarantee isolation
    /// so tests do not see each other's messages.
    /// </summary>
    protected abstract ITransportProviderPlaceholder CreateTransport();

    /// <summary>
    /// Publishing a message and receiving it round-trips the full envelope (headers + body)
    /// without loss, mutation, or re-encoding artefacts.
    /// </summary>
    [TestMethod]
    public Task Publish_RoundTripsMessageEnvelopeAsync() => Task.CompletedTask;

    /// <summary>
    /// The receiver observes the same <c>MessageId</c> the publisher set; transports must
    /// not generate or overwrite it.
    /// </summary>
    [TestMethod]
    public Task Publish_PreservesMessageIdAsync() => Task.CompletedTask;

    /// <summary>
    /// The receiver observes the same <c>CorrelationId</c> the publisher set, enabling
    /// end-to-end tracing across hops.
    /// </summary>
    [TestMethod]
    public Task Publish_PreservesCorrelationIdAsync() => Task.CompletedTask;

    /// <summary>
    /// The receiver observes the same <c>EventTypeId</c> the publisher set, so subscriber
    /// dispatch sees the canonical event-type identifier.
    /// </summary>
    [TestMethod]
    public Task Publish_PreservesEventTypeIdAsync() => Task.CompletedTask;

    /// <summary>
    /// For a single producer sending N messages with no session key, the receiver observes
    /// them in the order they were sent (FIFO from one producer).
    /// </summary>
    [TestMethod]
    public Task Receive_DeliversInOrderForSingleProducerAsync() => Task.CompletedTask;

    /// <summary>
    /// A batch publish of N messages results in all N being delivered to the receiver,
    /// with no drops, duplicates, or partial-batch failures silently swallowed.
    /// </summary>
    [TestMethod]
    public Task BatchPublish_DeliversAllMessagesAsync() => Task.CompletedTask;
}
