using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Transport.Abstractions;

namespace NimBus.Testing.Conformance.Transport;

/// <summary>
/// Provider-agnostic conformance for failure / dead-letter behaviour: a handler that
/// consistently fails exhausts retries and is dead-lettered, dead-letters surface as
/// <c>UnresolvedEvents</c>, the dead-letter reason is preserved in the audit trail,
/// and operator-resubmit re-enters the processing pipeline.
/// </summary>
/// <remarks>
/// Concrete provider runs subclass this type and override <see cref="CreateTransport"/>.
/// Test bodies are intentionally empty until the transport abstractions land
/// (task #2 / issue #17). Each method name is normative — the scenario it asserts is
/// described in its XML doc comment.
/// </remarks>
[TestClass]
public abstract class DeadLetterConformanceTests
{
    /// <summary>
    /// Returns a transport provider (or test-double) wired to an isolated topology with a
    /// handler whose failure mode the test controls.
    /// </summary>
    protected abstract ITransportProviderRegistration CreateTransport();

    /// <summary>
    /// A message whose handler throws on every delivery exhausts the configured retry
    /// budget and is moved to the dead-letter destination.
    /// </summary>
    [TestMethod]
    public Task Failure_ExhaustsRetries_DeadLettersAsync() => Task.CompletedTask;

    /// <summary>
    /// A dead-lettered message is projected into the message-tracking store as an
    /// <c>UnresolvedEvent</c> with <c>ResolutionStatus.DeadLettered</c>.
    /// </summary>
    [TestMethod]
    public Task DeadLetter_ProjectsToUnresolvedEventsAsync() => Task.CompletedTask;

    /// <summary>
    /// The transport-supplied reason / error description for a dead-lettered message is
    /// preserved in the audit trail (visible to operators in the management UI).
    /// </summary>
    [TestMethod]
    public Task DeadLetterReason_PreservedInAuditTrailAsync() => Task.CompletedTask;

    /// <summary>
    /// An operator-issued resubmit on a dead-lettered message returns it to the live
    /// processing pipeline so handlers see it again.
    /// </summary>
    [TestMethod]
    public Task Resubmit_ReentersProcessingAsync() => Task.CompletedTask;
}
