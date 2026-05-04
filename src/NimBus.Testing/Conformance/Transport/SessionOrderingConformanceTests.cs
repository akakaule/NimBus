using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Transport.Abstractions;

namespace NimBus.Testing.Conformance.Transport;

/// <summary>
/// Provider-agnostic conformance for session-key ordering guarantees: per-key FIFO,
/// across-key parallelism, and the SC-009 burst scenario (1000 messages across 100 keys).
/// </summary>
/// <remarks>
/// Concrete provider runs subclass this type and override <see cref="CreateTransport"/>.
/// Test bodies are intentionally empty until the transport abstractions land
/// (task #2 / issue #17). Each method name is normative — the scenario it asserts is
/// described in its XML doc comment.
/// </remarks>
[TestClass]
public abstract class SessionOrderingConformanceTests
{
    /// <summary>
    /// Returns a transport provider (or test-double) wired to an isolated topology with
    /// session-aware receiver(s).
    /// </summary>
    protected abstract ITransportProviderRegistration CreateTransport();

    /// <summary>
    /// Messages sent with the same session key are delivered to the receiver in the order
    /// they were sent (FIFO within a key).
    /// </summary>
    [TestMethod]
    public Task Send_SameSessionKey_PreservesFifoOrderAsync() => Task.CompletedTask;

    /// <summary>
    /// Messages sent with different session keys can be processed concurrently — the
    /// receiver must not serialize across keys.
    /// </summary>
    [TestMethod]
    public Task Send_DifferentSessionKeys_ProcessInParallelAsync() => Task.CompletedTask;

    /// <summary>
    /// SC-009: a burst of 1000 messages spread across 100 session keys — for each key the
    /// receiver observes per-key FIFO ordering of the 10 messages assigned to it.
    /// </summary>
    [TestMethod]
    public Task Burst_1000Messages_100Keys_PreservesPerKeyOrderAsync() => Task.CompletedTask;

    /// <summary>
    /// SC-009: same burst (1000 messages, 100 keys) — wall-clock latency does not scale
    /// linearly with burst size; concurrent processing across keys is observed.
    /// </summary>
    [TestMethod]
    public Task Burst_1000Messages_100Keys_AcrossKeyParallelismObservedAsync() => Task.CompletedTask;
}
