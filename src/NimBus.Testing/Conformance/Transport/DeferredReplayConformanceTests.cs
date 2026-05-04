using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Transport.Abstractions;

namespace NimBus.Testing.Conformance.Transport;

/// <summary>
/// Provider-agnostic conformance for the park-and-replay (deferred-by-session) pipeline:
/// blocking a session parks subsequent messages, unblocking replays them in FIFO order,
/// the park step is idempotent, and replay survives a crash.
/// </summary>
/// <remarks>
/// Concrete provider runs subclass this type and override <see cref="CreateTransport"/>.
/// Test bodies are intentionally empty until the transport abstractions land
/// (task #2 / issue #17) and the deferred-by-session work in NimBus.Core lands
/// (task #5 / issue #20). Each method name is normative — the scenario it asserts is
/// described in its XML doc comment.
/// </remarks>
[TestClass]
public abstract class DeferredReplayConformanceTests
{
    /// <summary>
    /// Returns a transport provider (or test-double) wired to an isolated topology with a
    /// receiver participating in the park-and-replay pipeline.
    /// </summary>
    protected abstract ITransportProviderRegistration CreateTransport();

    /// <summary>
    /// When a session is blocked, subsequent messages on that session are parked
    /// (deferred / set-aside) rather than dispatched to the handler.
    /// </summary>
    [TestMethod]
    public Task BlockSession_ParksSubsequentMessagesAsync() => Task.CompletedTask;

    /// <summary>
    /// When a previously blocked session is unblocked, its parked messages are replayed
    /// to the handler in the original FIFO send order — not lost, not reordered.
    /// </summary>
    [TestMethod]
    public Task UnblockSession_ReplaysParkedMessagesInFifoOrderAsync() => Task.CompletedTask;

    /// <summary>
    /// Parking a message with a previously-parked <c>MessageId</c> is a no-op: the parked
    /// set still contains exactly one entry for that id. Guards against double-park on
    /// retry / at-least-once delivery from upstream.
    /// </summary>
    [TestMethod]
    public Task Park_IsIdempotentOnDuplicateMessageIdAsync() => Task.CompletedTask;

    /// <summary>
    /// If the process crashes mid-replay, the next process startup resumes replay from
    /// where it left off without losing or double-processing parked messages.
    /// </summary>
    [TestMethod]
    public Task Replay_IsCrashResilientAsync() => Task.CompletedTask;

    /// <summary>
    /// An operator-issued skip on a parked message removes it from the parked set and
    /// emits an audit-trail entry recording the skip.
    /// </summary>
    [TestMethod]
    public Task OperatorSkip_RemovesParkedMessageAndEmitsAuditAsync() => Task.CompletedTask;
}
