using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.Testing.Conformance.Transport;

/// <summary>
/// Provider-agnostic conformance for the blocked-session lifecycle: <c>Block</c>
/// persists state, <c>IsSessionBlocked</c> reflects it, <c>Unblock</c> clears it,
/// <c>BlockedByEventId</c> identifies the blocking event, and per-session blocks are
/// independent.
/// </summary>
/// <remarks>
/// These operations are now store-backed (see spec section "Critical Design Insight" —
/// blocked-session state was previously misclassified as transport-shaped but lives in
/// the message store). The conformance suite verifies the *transport-level* behaviour
/// observable to the receive pipeline: a blocked session does not dispatch.
/// Test bodies are intentionally empty until the transport abstractions land
/// (task #2 / issue #17) and the disentanglement work lands (task #1 / issue #16).
/// Each method name is normative — the scenario it asserts is described in its XML doc.
/// </remarks>
[TestClass]
public abstract class BlockedSessionLifecycleConformanceTests
{
    // TODO: Once task #2 (issue #17) lands, change return type to ITransportProvider.
    /// <summary>
    /// Returns a transport provider (or test-double) wired to an isolated topology and a
    /// fresh blocked-session store.
    /// </summary>
    protected abstract ITransportProviderPlaceholder CreateTransport();

    /// <summary>
    /// Blocking a session persists the block such that a subsequent process restart still
    /// observes the session as blocked.
    /// </summary>
    [TestMethod]
    public Task Block_PersistsStateAsync() => Task.CompletedTask;

    /// <summary>
    /// After <c>Block</c>, <c>IsSessionBlocked</c> returns <c>true</c> for that session.
    /// </summary>
    [TestMethod]
    public Task IsSessionBlocked_ReturnsTrueAfterBlockAsync() => Task.CompletedTask;

    /// <summary>
    /// After <c>Unblock</c>, the persisted block state is removed and
    /// <c>IsSessionBlocked</c> returns <c>false</c>.
    /// </summary>
    [TestMethod]
    public Task Unblock_RemovesStateAsync() => Task.CompletedTask;

    /// <summary>
    /// <c>BlockedByEventId</c> returns the <c>EventId</c> of the event that caused the
    /// session to be blocked (the originating failure / blocker), so operators can trace
    /// back to root cause.
    /// </summary>
    [TestMethod]
    public Task BlockedByEventId_ReturnsCorrectEventIdAsync() => Task.CompletedTask;

    /// <summary>
    /// Blocking session A does not affect session B. Sessions block independently.
    /// </summary>
    [TestMethod]
    public Task MultipleSessions_BlockIndependentlyAsync() => Task.CompletedTask;
}
