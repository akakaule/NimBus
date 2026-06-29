#pragma warning disable CA1707, CA2007

using CrmErpDemo.Contracts.Events;
using EnrichmentAgent;
using EnrichmentAgent.Classification;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Agents;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using Newtonsoft.Json;

namespace EnrichmentAgent.Tests;

/// <summary>
/// Spec 022 Phase 3 — the demo's CI gate, now targeting the NimBus.Agents SDK seam. Proves the
/// enrichment chain IN MEMORY and deterministically (no ANTHROPIC_API_KEY, no emulator, no live
/// WebApp): it drives <see cref="EnrichmentHandler.HandleAsync"/> with the deterministic classifier
/// and validates the published payload against the enriched-event schema with NJsonSchema — exactly
/// the way <c>AgentImplementation.PostAgentPublishAsync</c> does — so the test FAILS if the agent
/// ever produces a schema-invalid <c>crm.contact.enriched.v1</c> event. The SDK loop mechanics
/// (receive/settle/retry) are covered separately in NimBus.Agents.Tests.
/// </summary>
[TestClass]
public class AgentEnrichmentSmokeTests
{
    [TestMethod]
    public async Task EnrichmentHandler_ProducesSchemaValidEnrichedEvent_MatchingClassifierOutput()
    {
        // ── Arrange: a realistic CrmContactCreated. The "tech" in the email domain steers the
        //    DeterministicContactClassifier to "Technology".
        var contact = new CrmContactCreated
        {
            ContactId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            AccountId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            FirstName = "Alice",
            LastName = "Smith",
            Email = "alice@techcorp.com",
            Phone = "+1-555-0100",
        };

        var context = new AgentContext<CrmContactCreated>
        {
            Input = contact,
            RawPayload = JsonConvert.SerializeObject(contact),
            EventTypeId = "CrmContactCreated",
            Coordinates = new HandoffCoordinates(
                EventId: "event-001",
                SessionId: "session-abc",
                MessageId: "message-001",
                EventTypeId: "CrmContactCreated",
                CorrelationId: "correlation-001",
                OriginatingMessageId: "origin-001"),
        };

        var classifier = new DeterministicContactClassifier();
        var handler = new EnrichmentHandler(classifier);

        // Independently compute the expected classifier output for THIS contact.
        var expected = await classifier.Classify(new ContactInput(
            ContactId: contact.ContactId.ToString(),
            FirstName: contact.FirstName,
            LastName: contact.LastName,
            Email: contact.Email,
            Phone: contact.Phone));

        // ── Act
        var result = await handler.HandleAsync(context, CancellationToken.None);

        // ── Assert 1: completes successfully, publishing exactly one enriched event.
        Assert.IsTrue(result.IsSuccess, "Handler should settle the handoff complete.");
        Assert.AreEqual(1, result.Publishes.Count, "Handler should publish exactly one enriched event.");
        var publish = result.Publishes[0];
        Assert.AreEqual(EnrichmentHandler.EnrichedEventTypeId, publish.EventTypeId);
        Assert.IsNull(publish.SessionId, "Handler leaves SessionId null so the SDK inherits the received session.");

        // ── Assert 2: the published payload is genuinely schema-VALID, validated the same way the
        //    WebApp does (stored schema + NJsonSchema, zero errors).
        var store = new InMemoryMessageStore();
        await store.DefineEventType(new EventSchema
        {
            EventTypeId = EnrichmentHandler.EnrichedEventTypeId,
            Name = "Enriched CRM Contact",
            JsonSchema = EnrichmentHandler.EnrichedSchema,
            AgentId = "smoke-agent",
            CreatedBy = "smoke-agent",
            CreatedUtc = DateTime.UtcNow,
        });
        var stored = await store.GetSchema(EnrichmentHandler.EnrichedEventTypeId)
            ?? throw new InvalidOperationException("schema was not stored.");
        var jsonSchema = await NJsonSchema.JsonSchema.FromJsonAsync(stored.JsonSchema, CancellationToken.None);
        var errors = jsonSchema.Validate(publish.Payload);
        Assert.AreEqual(0, errors.Count,
            "Published crm.contact.enriched.v1 payload must be schema-valid. NJsonSchema errors: " +
            string.Join(", ", errors.Select(e => $"{e.Path}: {e.Kind}")));

        // ── Assert 3: the enriched fields match the classifier output for the seeded contact.
        var consumed = JsonConvert.DeserializeObject<EnrichedContact>(publish.Payload)
            ?? throw new InvalidOperationException("enriched payload deserialized to null.");
        Assert.AreEqual(contact.ContactId.ToString(), consumed.ContactId, "contactId must round-trip from the seeded contact.");
        Assert.AreEqual(expected.Industry, consumed.Industry, "Enriched industry must match the classifier output.");
        Assert.AreEqual("Technology", consumed.Industry, "Email domain 'techcorp.com' should classify as Technology.");
        Assert.AreEqual(expected.LeadScore, consumed.LeadScore, "Enriched leadScore must match the classifier output.");
    }

    /// <summary>
    /// Payload shape the DataPlatform consumer deserializes — mirrors <c>EnrichedContact</c> in
    /// DataPlatform.Adapter.Functions/Handlers/EnrichedContactHandler.cs.
    /// </summary>
    private sealed record EnrichedContact(string ContactId, string Industry, int LeadScore, string? Rationale);
}
