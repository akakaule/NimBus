#pragma warning disable CA1707, CA2007

using CrmErpDemo.Contracts.Events;
using EnrichmentAgent;
using EnrichmentAgent.Bus;
using EnrichmentAgent.Classification;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using Newtonsoft.Json;

namespace EnrichmentAgent.Tests;

/// <summary>
/// Spec 022 Phase 3 — the demo's CI gate. Proves the agent enrichment chain end-to-end
/// IN MEMORY and deterministically (no ANTHROPIC_API_KEY, no emulator, no live WebApp).
///
/// This is the Phase-1 Task-12 analogue extended with the real classifier and REAL JSON
/// Schema validation: it drives <see cref="AgentLoopWorker.ProcessNextAsync"/> against a
/// realistic in-memory <see cref="IBusGateway"/> that
/// <list type="bullet">
///   <item>backs schema definition with a real <see cref="InMemoryMessageStore"/>
///         (which implements <see cref="IEventSchemaStore"/>), and</item>
///   <item>validates every published payload against the stored schema with NJsonSchema —
///         exactly the way the WebApp does in
///         <c>AgentImplementation.PostAgentPublishAsync</c> — so the test FAILS if the agent
///         ever produces a schema-invalid <c>crm.contact.enriched.v1</c> event.</item>
/// </list>
/// A captured consumer callback stands in for the DataPlatform consumer (Task D), proving
/// the enriched payload deserializes and carries the classifier's output downstream.
/// </summary>
[TestClass]
public class AgentEnrichmentSmokeTests
{
    [TestMethod]
    public async Task EnrichmentChain_EndToEnd_DefinesValidatesPublishesConsumesSettles()
    {
        // ── Arrange: a realistic CrmContactCreated parked on the Agent Zone. The "tech" in
        //    the email domain steers the DeterministicContactClassifier to "Technology".
        var contact = new CrmContactCreated
        {
            ContactId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            AccountId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            FirstName = "Alice",
            LastName = "Smith",
            Email = "alice@techcorp.com",
            Phone = "+1-555-0100",
        };

        var coords = new HandoffCoordinates(
            EventId: "event-001",
            SessionId: "session-abc",
            MessageId: "message-001",
            EventTypeId: AgentLoopWorker.SourceEventTypeId,
            CorrelationId: "correlation-001",
            OriginatingMessageId: "origin-001");

        var seeded = new ReceivedMessage(
            AgentLoopWorker.SourceEventTypeId,
            JsonConvert.SerializeObject(contact),
            coords);

        // The captured consumer = the DataPlatform consumer (Task D). It deserializes the
        // enriched payload exactly like EnrichedContactHandler does and records it.
        EnrichedContact? consumed = null;
        var bus = new ValidatingInMemoryBusGateway(
            consumer: payloadJson =>
            {
                consumed = JsonConvert.DeserializeObject<EnrichedContact>(payloadJson);
                return Task.CompletedTask;
            },
            inbox: new ReceivedMessage?[] { seeded, null });

        var classifier = new DeterministicContactClassifier();
        var worker = new AgentLoopWorker(bus, classifier, NullLogger<AgentLoopWorker>.Instance);

        // Independently compute what the classifier should produce for THIS contact, so the
        // downstream assertions pin the agent to the classifier's real output.
        var expected = await classifier.Classify(new ContactInput(
            ContactId: contact.ContactId.ToString(),
            FirstName: contact.FirstName,
            LastName: contact.LastName,
            Email: contact.Email,
            Phone: contact.Phone));

        // ── Act
        var processed = await worker.ProcessNextAsync(CancellationToken.None);

        // ── Assert 1: a message was processed.
        Assert.IsTrue(processed, "ProcessNextAsync should report true when a parked contact is processed.");

        // ── Assert 2: the enriched event type was defined with the correct schema.
        Assert.AreEqual(1, bus.Defines.Count, "Schema should be defined exactly once.");
        Assert.AreEqual(AgentLoopWorker.EnrichedEventTypeId, bus.Defines[0].EventTypeId);
        Assert.AreEqual(AgentLoopWorker.EnrichedSchema, bus.Defines[0].Schema, "Defined schema must match the agent's enriched-event schema.");
        Assert.AreEqual("Enriched CRM Contact", bus.Defines[0].Name);

        // ── Assert 3: the published payload is genuinely schema-VALID (NJsonSchema, zero errors).
        Assert.AreEqual(1, bus.Publishes.Count, "Enriched event should be published exactly once.");
        Assert.AreEqual(AgentLoopWorker.EnrichedEventTypeId, bus.Publishes[0].EventTypeId);
        Assert.AreEqual(0, bus.LastPublishValidationErrors.Count,
            "Published crm.contact.enriched.v1 payload must be schema-valid. NJsonSchema errors: " +
            string.Join(", ", bus.LastPublishValidationErrors.Select(e => $"{e.Path}: {e.Kind}")));

        // ── Assert 4: the DataPlatform consumer received the enriched event and its fields
        //    match the classifier's output for the seeded contact.
        Assert.IsNotNull(consumed, "The captured DataPlatform consumer must have received the enriched event.");
        Assert.AreEqual(contact.ContactId.ToString(), consumed.ContactId, "Enriched contactId must round-trip from the seeded contact.");
        Assert.AreEqual(expected.Industry, consumed.Industry, "Enriched industry must match the classifier output.");
        Assert.AreEqual("Technology", consumed.Industry, "Email domain 'techcorp.com' should classify as Technology.");
        Assert.AreEqual(expected.LeadScore, consumed.LeadScore, "Enriched leadScore must match the classifier output.");

        // ── Assert 5: the original handoff was settled with the seeded coordinates + "complete".
        Assert.AreEqual(1, bus.Settles.Count, "The handoff should be settled exactly once.");
        Assert.AreEqual(coords, bus.Settles[0].Coordinates, "Settle coordinates must match the received message.");
        Assert.AreEqual("complete", bus.Settles[0].Outcome, "The handoff should be settled as complete.");

        // Bonus: ordering — define before publish before settle (the demo's logical chain).
        Assert.IsTrue(bus.Calls.IndexOf("Define") < bus.Calls.IndexOf("Publish"), "Define must precede Publish.");
        Assert.IsTrue(bus.Calls.IndexOf("Publish") < bus.Calls.IndexOf("Settle"), "Publish must precede Settle.");
    }

    /// <summary>
    /// Payload shape the DataPlatform consumer deserializes — mirrors <c>EnrichedContact</c>
    /// in DataPlatform.Adapter.Functions/Handlers/EnrichedContactHandler.cs.
    /// </summary>
    private sealed record EnrichedContact(string ContactId, string Industry, int LeadScore, string? Rationale);

    /// <summary>
    /// A realistic in-memory <see cref="IBusGateway"/> that performs REAL schema validation.
    /// Schemas are stored in a real <see cref="InMemoryMessageStore"/> (an
    /// <see cref="IEventSchemaStore"/>); publishes are validated against the stored schema
    /// with NJsonSchema, exactly as <c>AgentImplementation.PostAgentPublishAsync</c> does.
    /// On a valid publish it records the event and forwards the payload to a captured consumer.
    /// </summary>
    private sealed class ValidatingInMemoryBusGateway : IBusGateway
    {
        private readonly InMemoryMessageStore _store = new();
        private readonly Queue<ReceivedMessage?> _inbox;
        private readonly Func<string, Task> _consumer;

        public ValidatingInMemoryBusGateway(Func<string, Task> consumer, ReceivedMessage?[] inbox)
        {
            _consumer = consumer;
            _inbox = new Queue<ReceivedMessage?>(inbox);
        }

        public List<string> Calls { get; } = new();
        public List<(string EventTypeId, string Schema, string? Name)> Defines { get; } = new();
        public List<(string EventTypeId, string Payload, string? SessionId)> Publishes { get; } = new();
        public List<(HandoffCoordinates Coordinates, string Outcome, string? Result)> Settles { get; } = new();

        /// <summary>Validation errors from the most recent <see cref="PublishAsync"/> (empty = valid).</summary>
        public IReadOnlyList<NJsonSchema.Validation.ValidationError> LastPublishValidationErrors { get; private set; } =
            Array.Empty<NJsonSchema.Validation.ValidationError>();

        public Task SubscribeAsync(string eventTypeId, CancellationToken ct = default)
        {
            Calls.Add("Subscribe");
            return Task.CompletedTask;
        }

        public async Task DefineEventTypeAsync(string eventTypeId, string jsonSchema, string? name, CancellationToken ct = default)
        {
            Calls.Add("Define");
            Defines.Add((eventTypeId, jsonSchema, name));

            // Persist into the real schema store so PublishAsync can validate against it.
            await _store.DefineEventType(new EventSchema
            {
                EventTypeId = eventTypeId,
                Name = name ?? eventTypeId,
                JsonSchema = jsonSchema,
                AgentId = "smoke-agent",
                CreatedBy = "smoke-agent",
                CreatedUtc = DateTime.UtcNow,
            });
        }

        public Task<ReceivedMessage?> ReceiveAsync(string? eventTypeId, int waitSeconds, CancellationToken ct = default)
        {
            Calls.Add("Receive");
            return Task.FromResult(_inbox.Count > 0 ? _inbox.Dequeue() : null);
        }

        public async Task PublishAsync(string eventTypeId, string payloadJson, string? sessionId, CancellationToken ct = default)
        {
            Calls.Add("Publish");

            // Mirror AgentImplementation.PostAgentPublishAsync: look up the stored schema,
            // parse it with NJsonSchema, and validate the payload string against it.
            var schema = await _store.GetSchema(eventTypeId)
                ?? throw new InvalidOperationException($"Unknown eventTypeId '{eventTypeId}' — publish before define.");

            var jsonSchema = await NJsonSchema.JsonSchema.FromJsonAsync(schema.JsonSchema, ct);
            LastPublishValidationErrors = jsonSchema.Validate(payloadJson).ToList();

            // A real broker rejects an invalid payload — so we do NOT record or forward it.
            // The test asserts LastPublishValidationErrors is empty, so any invalid payload fails.
            if (LastPublishValidationErrors.Count > 0)
                return;

            Publishes.Add((eventTypeId, payloadJson, sessionId));
            await _consumer(payloadJson);
        }

        public Task SettleAsync(HandoffCoordinates coordinates, string outcome, string? result, CancellationToken ct = default)
        {
            Calls.Add("Settle");
            Settles.Add((coordinates, outcome, result));
            return Task.CompletedTask;
        }
    }
}
