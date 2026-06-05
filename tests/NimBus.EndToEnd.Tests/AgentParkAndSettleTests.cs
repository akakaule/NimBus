#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.EndToEnd.Tests.Infrastructure;
using NimBus.SDK.EventHandlers;
using System.Linq;

namespace NimBus.EndToEnd.Tests;

/// <summary>
/// Spec 022 Phase 1 — the Agent Zone park path. Proves that a coded subscriber whose handler
/// parks a dynamically-typed agent event as Pending+Handoff (via <see cref="MarkPendingHandoffJsonHandler"/>)
/// reuses the existing pending-handoff plumbing: it emits a PendingHandoffResponse, so an external
/// agent can later pull the parked event over REST and settle it. No Service Bus lock is held.
/// </summary>
[TestClass]
public class AgentParkAndSettleTests
{
    [TestMethod]
    public async Task ParkedDynamicEvent_EmitsPendingHandoff()
    {
        var fixture = new EndToEndFixture();
        fixture.RegisterDynamicHandler("crm.contact.enriched.v1", () => new MarkPendingHandoffJsonHandler());

        await fixture.PublishBus.Send(new Message
        {
            To = "crm.contact.enriched.v1",
            EventTypeId = "crm.contact.enriched.v1",
            SessionId = "s1",
            CorrelationId = System.Guid.NewGuid().ToString(),
            MessageId = System.Guid.NewGuid().ToString(),
            RetryCount = 0,
            MessageType = MessageType.EventRequest,
            MessageContent = new MessageContent { EventContent = new EventContent { EventTypeId = "crm.contact.enriched.v1", EventJson = "{}" } },
        });
        await fixture.DeliverAll();

        Assert.IsTrue(fixture.ResponseBus.SentMessages.Any(m => m.MessageType == MessageType.PendingHandoffResponse),
            "Parked event must emit a PendingHandoffResponse.");
    }
}
