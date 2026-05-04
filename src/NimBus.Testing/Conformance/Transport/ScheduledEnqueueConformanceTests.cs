using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Transport.Abstractions;

namespace NimBus.Testing.Conformance.Transport;

// Capability gating: when CreateCapabilities().SupportsScheduledEnqueue == false this
// class must report all its tests as skipped (e.g. via [TestInitialize] calling
// Assert.Inconclusive, or a custom MSTest discovery hook). The "tests skipped, not
// failed" requirement is asserted by
// CapabilityGatingConformanceTests.UnsupportedFeature_TestsAreSkippedNotFailed.
/// <summary>
/// Provider-agnostic conformance for native scheduled enqueue (delayed delivery): a
/// scheduled message is delivered close to its target time, never before its target,
/// and a cancellation issued before the target prevents delivery.
/// </summary>
/// <remarks>
/// This category is OPTIONAL — gated by <c>ITransportCapabilities.SupportsScheduledEnqueue</c>.
/// Transports that do not natively schedule (the entire suite is skipped, not failed) must
/// still pass the capability-gating meta-tests in <see cref="CapabilityGatingConformanceTests"/>.
/// Test bodies are intentionally empty until the transport abstractions land
/// (task #2 / issue #17). Each method name is normative — the scenario it asserts is
/// described in its XML doc comment.
/// </remarks>
[TestClass]
public abstract class ScheduledEnqueueConformanceTests
{
    /// <summary>
    /// Returns a transport provider (or test-double) wired to an isolated topology with
    /// scheduled-enqueue capability declared.
    /// </summary>
    protected abstract ITransportProviderRegistration CreateTransport();

    /// <summary>
    /// A message scheduled for time T is delivered to the receiver within ~1 second of T
    /// (allowing for broker scheduling jitter; tighter bounds are provider-specific).
    /// </summary>
    [TestMethod]
    public Task Schedule_DeliversWithinOneSecondOfTargetAsync() => Task.CompletedTask;

    /// <summary>
    /// A message scheduled for time T is NOT delivered to the receiver before T — the
    /// transport must not prematurely release a scheduled message.
    /// </summary>
    [TestMethod]
    public Task Schedule_NotDeliveredBeforeTargetAsync() => Task.CompletedTask;

    /// <summary>
    /// Cancelling a scheduled message before its target time prevents the receiver from
    /// ever observing it.
    /// </summary>
    [TestMethod]
    public Task Cancel_PreventsDeliveryAsync() => Task.CompletedTask;
}
