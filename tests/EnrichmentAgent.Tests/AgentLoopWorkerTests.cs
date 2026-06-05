#pragma warning disable CA1707, CA2007

using EnrichmentAgent;
using EnrichmentAgent.Bus;
using EnrichmentAgent.Classification;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace EnrichmentAgent.Tests;

[TestClass]
public class AgentLoopWorkerTests
{
    private static AgentLoopWorker NewWorker(IBusGateway bus) =>
        new(bus, new DeterministicContactClassifier(), NullLogger<AgentLoopWorker>.Instance);

    private static ReceivedMessage SeedContactCreated(
        string contactId = "11111111-1111-1111-1111-111111111111",
        string sessionId = "session-abc",
        string eventId = "event-001")
    {
        // Realistic CrmContactCreated payload. The "tech" in the email domain steers the
        // DeterministicContactClassifier to the "Technology" industry.
        var payload =
            $"{{\"contactId\":\"{contactId}\",\"firstName\":\"Alice\",\"lastName\":\"Smith\"," +
            "\"email\":\"alice@techcorp.com\",\"phone\":\"+1-555-0100\"}";

        var coords = new HandoffCoordinates(
            EventId: eventId,
            SessionId: sessionId,
            MessageId: "message-001",
            EventTypeId: AgentLoopWorker.SourceEventTypeId,
            CorrelationId: "correlation-001",
            OriginatingMessageId: "origin-001");

        return new ReceivedMessage(AgentLoopWorker.SourceEventTypeId, payload, coords);
    }

    [TestMethod]
    public async Task ProcessNextAsync_ParkedContact_DefinesPublishesSettles_InOrder()
    {
        var seeded = SeedContactCreated();
        // One parked message, then nothing.
        var bus = new FakeBusGateway(seeded, null);
        var worker = NewWorker(bus);

        var processed = await worker.ProcessNextAsync(CancellationToken.None);

        Assert.IsTrue(processed, "ProcessNextAsync should report true when a message is processed.");

        // Exactly one of each mutating call.
        Assert.AreEqual(1, bus.Defines.Count, "Schema should be defined once.");
        Assert.AreEqual(1, bus.Publishes.Count, "Enriched event should be published once.");
        Assert.AreEqual(1, bus.Settles.Count, "Handoff should be settled once.");

        // Define targets the enriched event type.
        Assert.AreEqual(AgentLoopWorker.EnrichedEventTypeId, bus.Defines[0].EventTypeId);
        Assert.AreEqual("Enriched CRM Contact", bus.Defines[0].Name);
        Assert.AreEqual(AgentLoopWorker.EnrichedSchema, bus.Defines[0].Schema);

        // Publish targets the enriched event type with a session carried from the handoff.
        var publish = bus.Publishes[0];
        Assert.AreEqual(AgentLoopWorker.EnrichedEventTypeId, publish.EventTypeId);
        Assert.AreEqual(seeded.Coordinates.SessionId, publish.SessionId, "Published session should match the received handoff session.");

        // Published payload deserializes to the expected enriched fields.
        var enriched = JObject.Parse(publish.Payload);
        Assert.AreEqual("11111111-1111-1111-1111-111111111111",
            (string?)enriched["contactId"], "contactId should round-trip from the received contact.");
        var industry = (string?)enriched["industry"];
        Assert.IsFalse(string.IsNullOrWhiteSpace(industry), "Enriched payload must contain a non-empty industry.");
        Assert.AreEqual("Technology", industry, "Email domain 'techcorp.com' should classify as Technology.");
        var leadScore = (int?)enriched["leadScore"];
        Assert.IsNotNull(leadScore, "Enriched payload must contain a leadScore.");
        Assert.IsTrue(leadScore >= 0 && leadScore <= 100, $"leadScore must be 0..100, got {leadScore}.");

        // Settle echoes the received coordinates with a 'complete' outcome.
        var settle = bus.Settles[0];
        Assert.AreEqual("complete", settle.Outcome);
        Assert.AreEqual(seeded.Coordinates, settle.Coordinates, "Settle coordinates must match the received message.");

        // Ordering: Define -> Publish -> Settle.
        var define = bus.Calls.IndexOf("Define");
        var pub = bus.Calls.IndexOf("Publish");
        var set = bus.Calls.IndexOf("Settle");
        Assert.IsTrue(define >= 0 && pub >= 0 && set >= 0, "All three mutating calls should be recorded.");
        Assert.IsTrue(define < pub, "DefineEventType must happen before Publish.");
        Assert.IsTrue(pub < set, "Publish must happen before Settle.");
    }

    [TestMethod]
    public async Task ProcessNextAsync_NoMessage_ReturnsFalse_NoPublishOrSettle()
    {
        var bus = new FakeBusGateway(/* empty inbox -> ReceiveAsync yields null */);
        var worker = NewWorker(bus);

        var processed = await worker.ProcessNextAsync(CancellationToken.None);

        Assert.IsFalse(processed, "ProcessNextAsync should report false when nothing is parked.");
        Assert.AreEqual(0, bus.Defines.Count, "No schema should be defined when nothing is received.");
        Assert.AreEqual(0, bus.Publishes.Count, "Nothing should be published when nothing is received.");
        Assert.AreEqual(0, bus.Settles.Count, "Nothing should be settled when nothing is received.");
    }

    [TestMethod]
    public async Task ProcessNextAsync_DefinesSchema_OnlyOnce_AcrossTwoMessages()
    {
        var first = SeedContactCreated(contactId: "22222222-2222-2222-2222-222222222222", eventId: "event-A");
        var second = SeedContactCreated(contactId: "33333333-3333-3333-3333-333333333333", eventId: "event-B");
        var bus = new FakeBusGateway(first, second, null);
        var worker = NewWorker(bus);

        Assert.IsTrue(await worker.ProcessNextAsync(CancellationToken.None));
        Assert.IsTrue(await worker.ProcessNextAsync(CancellationToken.None));

        Assert.AreEqual(1, bus.Defines.Count, "Schema must be defined only once across two processed messages (guard flag).");
        Assert.AreEqual(2, bus.Publishes.Count, "Both messages should be published.");
        Assert.AreEqual(2, bus.Settles.Count, "Both messages should be settled.");
    }

    /// <summary>
    /// In-memory <see cref="IBusGateway"/> that records calls in order and replays a
    /// pre-seeded queue of received messages (null = nothing parked).
    /// </summary>
    private sealed class FakeBusGateway : IBusGateway
    {
        private readonly Queue<ReceivedMessage?> _inbox;

        public FakeBusGateway(params ReceivedMessage?[] inbox) => _inbox = new Queue<ReceivedMessage?>(inbox);

        public List<string> Calls { get; } = new();
        public List<(string EventTypeId, string Schema, string? Name)> Defines { get; } = new();
        public List<(string EventTypeId, string Payload, string? SessionId)> Publishes { get; } = new();
        public List<(HandoffCoordinates Coordinates, string Outcome, string? Result)> Settles { get; } = new();
        public List<string> Subscribes { get; } = new();

        public Task SubscribeAsync(string eventTypeId, CancellationToken ct = default)
        {
            Calls.Add("Subscribe");
            Subscribes.Add(eventTypeId);
            return Task.CompletedTask;
        }

        public Task DefineEventTypeAsync(string eventTypeId, string jsonSchema, string? name, CancellationToken ct = default)
        {
            Calls.Add("Define");
            Defines.Add((eventTypeId, jsonSchema, name));
            return Task.CompletedTask;
        }

        public Task<ReceivedMessage?> ReceiveAsync(string? eventTypeId, int waitSeconds, CancellationToken ct = default)
        {
            Calls.Add("Receive");
            var msg = _inbox.Count > 0 ? _inbox.Dequeue() : null;
            return Task.FromResult(msg);
        }

        public Task PublishAsync(string eventTypeId, string payloadJson, string? sessionId, CancellationToken ct = default)
        {
            Calls.Add("Publish");
            Publishes.Add((eventTypeId, payloadJson, sessionId));
            return Task.CompletedTask;
        }

        public Task SettleAsync(HandoffCoordinates coordinates, string outcome, string? result, CancellationToken ct = default)
        {
            Calls.Add("Settle");
            Settles.Add((coordinates, outcome, result));
            return Task.CompletedTask;
        }
    }
}
