#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.Core.Transform;
using NimBus.EndToEnd.Tests.Infrastructure;
using NimBus.MappingExecutor;
using NimBus.MessageStore.States;
using NimBus.SDK;
using NimBus.Testing.Conformance;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.EndToEnd.Tests;

/// <summary>
/// Spec 023 — in-memory integration gate for the Mapping Executor runtime.
/// Exercises the REAL <see cref="MappingExecutorHandler"/>, <see cref="HandoffParkSink"/>,
/// and <see cref="PublisherTargetPublisher"/> wired through the EndToEnd harness
/// (StrictMessageHandler + EventHandlerProvider) on the in-memory transport.
///
/// These tests validate Task 9's executor wiring: the seams behave correctly together
/// without Azure Service Bus or a running WebApp.
/// </summary>
[TestClass]
public class MappingExecutorEndToEndTests
{
    // ── Event type ids ────────────────────────────────────────────────────────
    private const string SourceEventTypeId = "marketing.lead.created.v1";
    private const string TargetEventTypeId = "erp.customer.upsert.v1";

    // ── Source schema: leadId, company, email ─────────────────────────────────
    private const string SourceSchemaJson =
        "{\"type\":\"object\",\"required\":[\"leadId\",\"company\",\"email\"]," +
        "\"properties\":{\"leadId\":{\"type\":\"string\"}," +
        "\"company\":{\"type\":\"string\"},\"email\":{\"type\":\"string\"}}}";

    // ── Target schema: customerId, companyName, email ─────────────────────────
    private const string TargetSchemaJson =
        "{\"type\":\"object\",\"required\":[\"customerId\",\"companyName\",\"email\"]," +
        "\"properties\":{\"customerId\":{\"type\":\"string\"}," +
        "\"companyName\":{\"type\":\"string\"},\"email\":{\"type\":\"string\"}}}";

    // JSONata that maps the source to a valid target.
    private const string ValidTransform =
        "{ \"customerId\": leadId, \"companyName\": company, \"email\": email }";

    // ── Happy-path: Active mapping → transform → publish target ──────────────

    /// <summary>
    /// Publishes a <c>marketing.lead.created.v1</c> source event into a fixture wired with
    /// the real <see cref="MappingExecutorHandler"/> (Active mapping + valid JSONata transform).
    /// ASSERTS:
    /// <list type="bullet">
    ///   <item>A <c>erp.customer.upsert.v1</c> target event is published onto the target bus.</item>
    ///   <item>The target payload contains the mapped fields.</item>
    ///   <item>The source message is <em>completed</em> (not dead-lettered or stuck).</item>
    /// </list>
    /// </summary>
    [TestMethod]
    public async Task ActiveMapping_TransformsAndPublishes_TargetEvent_SourceCompleted()
    {
        // ── Arrange ──────────────────────────────────────────────────────────
        var store = await BuildStore(MappingState.Active);

        // Target bus captures what PublisherTargetPublisher publishes.
        var targetBus = new InMemoryBus();
        var publisherClient = new PublisherClient(targetBus);
        var targetPublisher = new PublisherTargetPublisher(publisherClient);

        var parkSink = new HandoffParkSink(NullLogger<HandoffParkSink>.Instance);

        var executorHandler = new MappingExecutorHandler(
            store, store,
            new JsonataTransformEngine(),
            targetPublisher,
            parkSink,
            NullLogger<MappingExecutorHandler>.Instance);

        var fixture = new EndToEndFixture();
        fixture.RegisterFallbackHandler(() => executorHandler);

        var sourceJson = "{\"leadId\":\"L-001\",\"company\":\"Acme Corp\",\"email\":\"alice@acme.com\"}";

        // ── Act ───────────────────────────────────────────────────────────────
        await fixture.PublishBus.Send(BuildSourceMessage(SourceEventTypeId, "session-e2e-1", sourceJson));
        var results = await fixture.DeliverAllWithResults();

        // ── Assert: source settled correctly ─────────────────────────────────
        Assert.AreEqual(1, results.Count, "Exactly one source message must be delivered.");
        var result = results.Single();
        Assert.IsNull(result.Exception, $"Handler must not throw: {result.Exception}");
        Assert.IsTrue(result.Session.WasCompleted, "Active-mapping source message must be completed.");
        Assert.IsFalse(result.Session.WasDeadLettered, "Active-mapping source message must not be dead-lettered.");

        // ── Assert: target event published ───────────────────────────────────
        var targetMessages = targetBus.SentMessages;
        Assert.AreEqual(1, targetMessages.Count,
            "Exactly one target event must be published by the executor.");

        var targetMsg = targetMessages.Single();
        Assert.AreEqual(TargetEventTypeId, targetMsg.EventTypeId,
            "Published target event must carry the target EventTypeId.");
        Assert.AreEqual(MessageType.EventRequest, targetMsg.MessageType,
            "Published target event must be an EventRequest.");

        var targetJson = targetMsg.MessageContent.EventContent.EventJson;
        StringAssert.Contains(targetJson, "customerId",
            "Target payload must contain 'customerId' field after mapping.");
        StringAssert.Contains(targetJson, "L-001",
            "Target payload must contain the mapped leadId value.");
        StringAssert.Contains(targetJson, "Acme Corp",
            "Target payload must contain the mapped company value.");
        StringAssert.Contains(targetJson, "alice@acme.com",
            "Target payload must contain the mapped email value.");

        // ── Assert: no park ───────────────────────────────────────────────────
        Assert.IsFalse(
            fixture.ResponseBus.SentMessages.Any(m => m.MessageType == MessageType.PendingHandoffResponse),
            "A successful active mapping must not park the source message.");
    }

    // ── Paused path: Paused mapping → park (Pending+Handoff) ─────────────────

    /// <summary>
    /// Publishes a source event when the mapping is <see cref="MappingState.Paused"/>.
    /// ASSERTS:
    /// <list type="bullet">
    ///   <item>The source is PARKED: a <see cref="MessageType.PendingHandoffResponse"/> is emitted.</item>
    ///   <item>The source Service Bus message is completed (lock-free park — same as AgentZone).</item>
    ///   <item>NO target event is published.</item>
    /// </list>
    /// </summary>
    [TestMethod]
    public async Task PausedMapping_Parks_SourceMessage_NoTargetPublished()
    {
        // ── Arrange ──────────────────────────────────────────────────────────
        var store = await BuildStore(MappingState.Paused);

        var targetBus = new InMemoryBus();
        var publisherClient = new PublisherClient(targetBus);
        var targetPublisher = new PublisherTargetPublisher(publisherClient);

        var parkSink = new HandoffParkSink(NullLogger<HandoffParkSink>.Instance);

        var executorHandler = new MappingExecutorHandler(
            store, store,
            new JsonataTransformEngine(),
            targetPublisher,
            parkSink,
            NullLogger<MappingExecutorHandler>.Instance);

        var fixture = new EndToEndFixture();
        fixture.RegisterFallbackHandler(() => executorHandler);

        var sourceJson = "{\"leadId\":\"L-002\",\"company\":\"Beta Ltd\",\"email\":\"bob@beta.com\"}";

        // ── Act ───────────────────────────────────────────────────────────────
        await fixture.PublishBus.Send(BuildSourceMessage(SourceEventTypeId, "session-e2e-2", sourceJson));
        var results = await fixture.DeliverAllWithResults();

        // ── Assert: parked (Pending+Handoff) ──────────────────────────────────
        Assert.AreEqual(1, results.Count, "Exactly one source message must be delivered.");
        var result = results.Single();
        Assert.IsNull(result.Exception, $"Handler must not throw on park: {result.Exception}");

        Assert.IsTrue(
            fixture.ResponseBus.SentMessages.Any(m => m.MessageType == MessageType.PendingHandoffResponse),
            "Paused mapping must emit a PendingHandoffResponse (park path).");

        // Lock-free park: SB message is completed even while session is blocked.
        Assert.IsTrue(result.Session.WasCompleted,
            "Parked source message must complete the Service Bus message (lock-free park).");
        Assert.IsFalse(result.Session.WasDeadLettered,
            "Parked source message must not be dead-lettered.");

        // ── Assert: no target published ───────────────────────────────────────
        Assert.AreEqual(0, targetBus.SentMessages.Count,
            "No target event must be published when the mapping is Paused.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds an <see cref="InMemoryMessageStore"/> with source and target schemas and a mapping
    /// whose <see cref="EventMapping.SourceSchemaHash"/> matches the source schema so the drift guard
    /// passes.
    /// </summary>
    private static async Task<InMemoryMessageStore> BuildStore(MappingState state)
    {
        var store = new InMemoryMessageStore();

        await store.DefineEventType(new EventSchema
        {
            EventTypeId = SourceEventTypeId,
            Name = SourceEventTypeId,
            JsonSchema = SourceSchemaJson,
            Version = 1,
            AgentId = "test",
            CreatedUtc = DateTime.UtcNow,
        });

        await store.DefineEventType(new EventSchema
        {
            EventTypeId = TargetEventTypeId,
            Name = TargetEventTypeId,
            JsonSchema = TargetSchemaJson,
            Version = 1,
            AgentId = "test",
            CreatedUtc = DateTime.UtcNow,
        });

        await store.SaveMapping(new EventMapping
        {
            Id = $"{SourceEventTypeId}->{TargetEventTypeId}",
            SourceEventTypeId = SourceEventTypeId,
            TargetEventTypeId = TargetEventTypeId,
            Transform = ValidTransform,
            // SourceSchemaHash must match the stored source schema so the drift guard passes.
            SourceSchemaHash = SchemaHash.Of(SourceSchemaJson),
            State = state,
            Version = 1,
            CreatedBy = "test",
            CreatedUtc = DateTime.UtcNow,
        });

        return store;
    }

    /// <summary>
    /// Builds an in-memory <see cref="IMessage"/> simulating a source event arriving at the
    /// Mapping Zone subscriber (mirrors <c>EnrichedContactHandlerTests.CreateEnrichedEventRequest</c>).
    /// </summary>
    private static IMessage BuildSourceMessage(string eventTypeId, string sessionId, string json)
    {
        return new Message
        {
            To = eventTypeId,
            EventTypeId = eventTypeId,
            SessionId = sessionId,
            CorrelationId = Guid.NewGuid().ToString(),
            MessageId = Guid.NewGuid().ToString(),
            RetryCount = 0,
            MessageType = MessageType.EventRequest,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = eventTypeId,
                    EventJson = json,
                },
            },
        };
    }
}
