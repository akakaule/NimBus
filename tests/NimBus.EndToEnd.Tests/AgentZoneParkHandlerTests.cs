#pragma warning disable CA1707, CA2007
using CrmErpDemo.Contracts.Events;
using CrmErpDemo.Contracts.Handlers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.EndToEnd.Tests.Infrastructure;
using System.Linq;

namespace NimBus.EndToEnd.Tests;

/// <summary>
/// Spec 022 Phase 3C — proves that the typed <see cref="AgentZoneParkHandler"/>
/// parks a <see cref="CrmContactCreated"/> event as Pending+Handoff via the
/// standard EndToEnd harness, matching what the hosted AgentZone worker does at runtime.
/// </summary>
[TestClass]
public class AgentZoneParkHandlerTests
{
    [TestMethod]
    public async Task TypedParkHandler_CrmContactCreated_EmitsPendingHandoff_AndCompletes()
    {
        // Arrange — wire the typed AgentZoneParkHandler exactly as the hosted worker does.
        var fixture = new EndToEndFixture();
        fixture.RegisterHandler<CrmContactCreated>(() => new AgentZoneParkHandler());

        var @event = new CrmContactCreated
        {
            ContactId = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            FirstName = "Alice",
            LastName = "Smith",
            Email = "alice@example.com",
        };

        // Act — Publish uses @event.GetSessionId() (ContactId via [SessionKey]) automatically.
        await fixture.Publisher.Publish(@event);
        var results = await fixture.DeliverAllWithResults();

        // Assert — the park handler must emit a PendingHandoffResponse so the agent
        // REST endpoint (/api/agent/receive) can pull and settle the parked event.
        Assert.IsTrue(
            fixture.ResponseBus.SentMessages.Any(m => m.MessageType == MessageType.PendingHandoffResponse),
            "AgentZoneParkHandler must emit a PendingHandoffResponse for the parked CrmContactCreated.");

        // No SB lock is held while parked: StrictMessageHandler completes the SB message
        // immediately after emitting the response and blocking the session.
        var result = results.Single();
        Assert.IsTrue(result.Session.WasCompleted, "Parked CrmContactCreated must complete the Service Bus message.");
        Assert.IsFalse(result.Session.WasDeadLettered, "Parked CrmContactCreated must not be dead-lettered.");
    }
}
