using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.Testing.Conformance.Transport;

/// <summary>
/// Meta-tests for the conformance suite itself: capability-gated test categories
/// (e.g. <see cref="ScheduledEnqueueConformanceTests"/>) are SKIPPED rather than FAILED
/// when the transport does not declare support for the gated feature, and the capability
/// flags reported by a transport accurately describe what it actually supports.
/// </summary>
/// <remarks>
/// Unlike the other conformance classes in this directory, this class is not gated by
/// any capability — it asserts the gating mechanism itself works. Concrete transport
/// runs MUST include this class so a misconfigured provider (e.g. claiming a capability
/// it does not implement) fails CI loudly.
/// Test bodies are intentionally empty until the transport abstractions land
/// (task #2 / issue #17). Each method name is normative — the scenario it asserts is
/// described in its XML doc comment.
/// </remarks>
[TestClass]
public abstract class CapabilityGatingConformanceTests
{
    // TODO: Once task #2 (issue #17) lands, change return types to ITransportProvider /
    // ITransportCapabilities.
    /// <summary>
    /// Returns a transport provider whose declared capabilities are under test.
    /// </summary>
    protected abstract ITransportProviderPlaceholder CreateTransport();

    /// <summary>
    /// Returns the capability descriptor for the transport returned by
    /// <see cref="CreateTransport"/>. Asserted against actual transport behaviour.
    /// </summary>
    protected abstract ITransportCapabilitiesPlaceholder CreateCapabilities();

    /// <summary>
    /// When a transport declares a feature unsupported (e.g.
    /// <c>SupportsScheduledEnqueue == false</c>), the corresponding conformance category
    /// reports its tests as SKIPPED in the test runner output — not FAILED, not silently
    /// passing. This is the load-bearing assertion that the gating mechanism is wired
    /// correctly.
    /// </summary>
    [TestMethod]
    public Task UnsupportedFeature_TestsAreSkippedNotFailedAsync() => Task.CompletedTask;

    /// <summary>
    /// Every flag a transport claims on its <c>ITransportCapabilities</c> is observed in
    /// real behaviour — e.g. a transport that claims <c>SupportsScheduledEnqueue</c>
    /// actually delivers a scheduled message. Catches drift between declared and actual
    /// capability.
    /// </summary>
    [TestMethod]
    public Task CapabilityFlags_AccuratelyReportTransportSupportAsync() => Task.CompletedTask;
}
