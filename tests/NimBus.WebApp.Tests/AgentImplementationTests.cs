#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Manager;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using IMessage = global::NimBus.Core.Messages.IMessage;
using MessageType = global::NimBus.Core.Messages.MessageType;
using MessageEntity = global::NimBus.MessageStore.MessageEntity;
using UnresolvedEvent = global::NimBus.MessageStore.UnresolvedEvent;

namespace NimBus.WebApp.Tests
{
    [TestClass]
    public class AgentImplementationTests
    {
        // ── Minimal fake IPlatform ──────────────────────────────────────────

        private sealed class FakePlatform : IPlatform
        {
            private readonly List<IEndpoint> _endpoints;

            public FakePlatform(params string[] endpointIds)
            {
                _endpoints = endpointIds.Select(id => (IEndpoint)new FakeEndpoint(id)).ToList();
            }

            public IEnumerable<IEndpoint> Endpoints => _endpoints;
            public IEnumerable<IEventType> EventTypes => Enumerable.Empty<IEventType>();
            public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) => Enumerable.Empty<IEndpoint>();
            public IEnumerable<IEndpoint> GetProducers(IEventType eventType) => Enumerable.Empty<IEndpoint>();
        }

        private sealed class FakeEndpoint : IEndpoint
        {
            public FakeEndpoint(string id) { Id = id; }

            public string Id { get; }
            public string Name => Id;
            public string Description => string.Empty;
            public string Namespace => "Tests";
            public string SecurityGroupName => string.Empty;
            public NimBus.Core.Endpoints.ISystem System => null!;
            public IEnumerable<IEventType> EventTypesConsumed => Enumerable.Empty<IEventType>();
            public IEnumerable<IEventType> EventTypesProduced => Enumerable.Empty<IEventType>();
            public IEnumerable<NimBus.Core.Endpoints.IRoleAssignment> RoleAssignments => Enumerable.Empty<NimBus.Core.Endpoints.IRoleAssignment>();
        }

        // ── Fake publisher (captures the published Message) ─────────────────

        private sealed class CapturingPublisher : IAgentEventPublisher
        {
            public IMessage? Published { get; private set; }
            public int CallCount { get; private set; }

            public Task PublishAsync(IMessage message, CancellationToken cancellationToken = default)
            {
                Published = message;
                CallCount++;
                return Task.CompletedTask;
            }
        }

        // ── Fake IManagerClient (captures handoff settlements) ──────────────

        private sealed class CapturingManagerClient : IManagerClient
        {
            public MessageEntity? CompletedEntry { get; private set; }
            public string? CompletedEndpoint { get; private set; }
            public string? CompletedDetails { get; private set; }
            public int CompleteCount { get; private set; }

            public MessageEntity? FailedEntry { get; private set; }
            public string? FailedEndpoint { get; private set; }
            public string? FailedErrorText { get; private set; }
            public string? FailedErrorType { get; private set; }
            public int FailCount { get; private set; }

            public Task Resubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson)
                => throw new NotImplementedException();

            public Task Skip(MessageEntity errorResponse, string endpoint, string eventTypeId)
                => throw new NotImplementedException();

            public Task CompleteHandoff(MessageEntity pendingEntry, string endpoint, string? detailsJson = null)
            {
                CompletedEntry = pendingEntry;
                CompletedEndpoint = endpoint;
                CompletedDetails = detailsJson;
                CompleteCount++;
                return Task.CompletedTask;
            }

            public Task FailHandoff(MessageEntity pendingEntry, string endpoint, string errorText, string? errorType = null)
            {
                FailedEntry = pendingEntry;
                FailedEndpoint = endpoint;
                FailedErrorText = errorText;
                FailedErrorType = errorType;
                FailCount++;
                return Task.CompletedTask;
            }
        }

        // Store whose GetPendingEventsOnSession returns null — mirrors the real Cosmos
        // provider when the zone container doesn't exist yet. The real InMemoryMessageStore
        // returns an empty sequence and so cannot exercise the null-guard.
        private sealed class NullPendingStore : InMemoryMessageStore
        {
            public override Task<IEnumerable<UnresolvedEvent>> GetPendingEventsOnSession(string endpointId)
                => Task.FromResult<IEnumerable<UnresolvedEvent>>(null!);
        }

        // The Agent Zone endpoint id receive/settle resolve to when no IConfiguration is
        // supplied (AgentZone.ResolveEndpointId(null) -> the default). Tests seed parked
        // events under this endpoint id.
        private const string ZoneId = AgentZone.DefaultAgentZoneEndpointId;

        // ── Builder ─────────────────────────────────────────────────────────

        private static (AgentImplementation Impl, InMemoryMessageStore Store) Build(
            params string[] endpointIds)
        {
            var (impl, store, _, _, _) = BuildAgent(endpointIds);
            return (impl, store);
        }

        private static (AgentImplementation Impl, InMemoryMessageStore Store, CapturingPublisher Publisher) BuildWithPublisher(
            params string[] endpointIds)
        {
            var (impl, store, publisher, _, _) = BuildAgent(endpointIds);
            return (impl, store, publisher);
        }

        private static (AgentImplementation Impl, InMemoryMessageStore Store, CapturingPublisher Publisher, CapturingManagerClient Manager, AgentSubscriptionRegistry Registry) BuildAgent(
            params string[] endpointIds)
        {
            var store = new InMemoryMessageStore();
            var platform = new FakePlatform(endpointIds);
            var publisher = new CapturingPublisher();
            var manager = new CapturingManagerClient();
            var registry = new AgentSubscriptionRegistry();
            var impl = new AgentImplementation(
                store,
                platform,
                publisher,
                store,
                manager,
                registry,
                config: null,                 // -> AgentZone default zone id
                httpContextAccessor: null,    // -> CurrentAgentId() falls back to "demo-agent"
                NullLogger<AgentImplementation>.Instance);
            return (impl, store, publisher, manager, registry);
        }

        // Seeds a Pending+Handoff event under the Agent Zone endpoint, exactly as Task 7's
        // Agent Zone subscriber parks each agent event.
        private static Task SeedPendingHandoff(
            InMemoryMessageStore store,
            string eventId,
            string sessionId,
            string eventTypeId,
            string payload,
            string messageId = "msg-1",
            string correlationId = "corr-1",
            string originatingMessageId = "orig-1")
        {
            return store.UploadPendingMessage(eventId, sessionId, ZoneId, new UnresolvedEvent
            {
                EventTypeId = eventTypeId,
                LastMessageId = messageId,
                CorrelationId = correlationId,
                OriginatingMessageId = originatingMessageId,
                PendingSubStatus = "Handoff",
                MessageContent = new global::NimBus.Core.Messages.MessageContent
                {
                    EventContent = new global::NimBus.Core.Messages.EventContent
                    {
                        EventTypeId = eventTypeId,
                        EventJson = payload,
                    },
                },
            });
        }

        private static DefineEventTypeRequest ValidRequest(
            string eventTypeId = "test.event.v1",
            string jsonSchema = "{\"type\":\"object\"}",
            string? name = null,
            string? description = null)
        {
            return new DefineEventTypeRequest
            {
                EventTypeId = eventTypeId,
                JsonSchema = jsonSchema,
                Name = name ?? eventTypeId,
                Description = description,
            };
        }

        // ── GetAgentCatalogAsync ─────────────────────────────────────────────

        [TestMethod]
        public async Task GetAgentCatalog_returns_endpoints_and_event_types()
        {
            var (impl, store) = Build("ep-alpha", "ep-beta");

            // Pre-seed a schema directly via the store
            await store.DefineEventType(new NimBus.MessageStore.States.EventSchema
            {
                EventTypeId = "crm.contact.created.v1",
                Name = "Contact Created",
                JsonSchema = "{\"type\":\"object\"}",
                Version = 1,
                AgentId = "test",
                CreatedUtc = DateTime.UtcNow,
            });

            var result = await impl.GetAgentCatalogAsync();

            var ok = result.Result as OkObjectResult;
            Assert.IsNotNull(ok, "Expected OkObjectResult");

            var catalog = ok!.Value as AgentCatalog;
            Assert.IsNotNull(catalog);

            var expectedEndpoints = new[] { "ep-alpha", "ep-beta" };
            CollectionAssert.AreEquivalent(
                expectedEndpoints,
                catalog!.Endpoints.ToArray(),
                "Endpoint ids must be listed");

            Assert.AreEqual(1, catalog.EventTypes.Count, "One event type expected");
            Assert.AreEqual("crm.contact.created.v1", catalog.EventTypes[0].EventTypeId);
            Assert.AreEqual("Contact Created", catalog.EventTypes[0].Name);
        }

        [TestMethod]
        public async Task GetAgentCatalog_empty_store_returns_empty_lists()
        {
            var (impl, _) = Build();

            var result = await impl.GetAgentCatalogAsync();

            var ok = result.Result as OkObjectResult;
            Assert.IsNotNull(ok);
            var catalog = ok!.Value as AgentCatalog;
            Assert.IsNotNull(catalog);
            Assert.AreEqual(0, catalog!.Endpoints.Count);
            Assert.AreEqual(0, catalog.EventTypes.Count);
        }

        // ── PostAgentEventTypesAsync — success / idempotent ─────────────────

        [TestMethod]
        public async Task PostAgentEventTypes_valid_request_returns_200_EventTypeInfo()
        {
            var (impl, _) = Build();
            // Name omitted so the implementation's Name fallback (=> EventTypeId) is exercised.
            var req = new DefineEventTypeRequest
            {
                EventTypeId = "order.placed.v1",
                JsonSchema = "{\"type\":\"object\"}",
            };

            var result = await impl.PostAgentEventTypesAsync(req);

            var ok = result.Result as OkObjectResult;
            Assert.IsNotNull(ok, "Expected OkObjectResult (200)");

            var info = ok!.Value as EventTypeInfo;
            Assert.IsNotNull(info);
            Assert.AreEqual("order.placed.v1", info!.EventTypeId);
            // Name fallback: a null request Name becomes the EventTypeId.
            Assert.AreEqual("order.placed.v1", info.Name);
            // JsonSchema round-trips unchanged.
            Assert.AreEqual("{\"type\":\"object\"}", info.JsonSchema);
        }

        [TestMethod]
        public async Task PostAgentEventTypes_same_schema_twice_is_idempotent_returns_200()
        {
            var (impl, _) = Build();
            var req = ValidRequest(eventTypeId: "order.placed.v1", jsonSchema: "{\"type\":\"object\"}");

            var first = await impl.PostAgentEventTypesAsync(req);
            var second = await impl.PostAgentEventTypesAsync(req);

            Assert.IsInstanceOfType(first.Result, typeof(OkObjectResult), "First call must be 200");
            Assert.IsInstanceOfType(second.Result, typeof(OkObjectResult), "Second call with same schema must be 200");
        }

        [TestMethod]
        public async Task PostAgentEventTypes_then_GetAgentCatalog_round_trips_the_event_type()
        {
            // Same controller instance + same backing store: a defined event type
            // must subsequently appear in the catalog.
            var (impl, _) = Build("ep-alpha");
            var req = ValidRequest(
                eventTypeId: "inventory.adjusted.v1",
                jsonSchema: "{\"type\":\"object\"}",
                name: "Inventory Adjusted");

            var define = await impl.PostAgentEventTypesAsync(req);
            Assert.IsInstanceOfType(define.Result, typeof(OkObjectResult), "Define must be 200");

            var catalogResult = await impl.GetAgentCatalogAsync();
            var catalog = (catalogResult.Result as OkObjectResult)!.Value as AgentCatalog;
            Assert.IsNotNull(catalog);

            var entry = catalog!.EventTypes.SingleOrDefault(e => e.EventTypeId == "inventory.adjusted.v1");
            Assert.IsNotNull(entry, "Defined event type must appear in the catalog");
            Assert.AreEqual("Inventory Adjusted", entry!.Name);
        }

        // ── PostAgentEventTypesAsync — conflict ─────────────────────────────

        [TestMethod]
        public async Task PostAgentEventTypes_different_schema_returns_409()
        {
            var (impl, _) = Build();
            var first = ValidRequest(eventTypeId: "order.placed.v1", jsonSchema: "{\"type\":\"object\"}");
            var conflict = ValidRequest(eventTypeId: "order.placed.v1", jsonSchema: "{\"type\":\"string\"}");

            await impl.PostAgentEventTypesAsync(first);
            var result = await impl.PostAgentEventTypesAsync(conflict);

            Assert.IsInstanceOfType(result.Result, typeof(ConflictResult),
                "Changing a schema must yield 409 ConflictResult");
        }

        // ── PostAgentEventTypesAsync — validation ────────────────────────────

        [TestMethod]
        public async Task PostAgentEventTypes_empty_eventTypeId_returns_400()
        {
            var (impl, _) = Build();
            var req = ValidRequest(eventTypeId: "");

            var result = await impl.PostAgentEventTypesAsync(req);

            Assert.IsInstanceOfType(result.Result, typeof(BadRequestObjectResult),
                "Empty eventTypeId must yield 400");
        }

        [TestMethod]
        public async Task PostAgentEventTypes_whitespace_eventTypeId_returns_400()
        {
            var (impl, _) = Build();
            var req = ValidRequest(eventTypeId: "   ");

            var result = await impl.PostAgentEventTypesAsync(req);

            Assert.IsInstanceOfType(result.Result, typeof(BadRequestObjectResult),
                "Whitespace-only eventTypeId must yield 400");
        }

        [TestMethod]
        public async Task PostAgentEventTypes_empty_jsonSchema_returns_400()
        {
            var (impl, _) = Build();
            var req = ValidRequest(jsonSchema: "");

            var result = await impl.PostAgentEventTypesAsync(req);

            Assert.IsInstanceOfType(result.Result, typeof(BadRequestObjectResult),
                "Empty jsonSchema must yield 400");
        }

        [TestMethod]
        public async Task PostAgentEventTypes_unparseable_jsonSchema_returns_400()
        {
            var (impl, store) = Build();
            var req = ValidRequest(eventTypeId: "bad.schema.v1", jsonSchema: "{ this is not valid json schema");

            var result = await impl.PostAgentEventTypesAsync(req);

            Assert.IsInstanceOfType(result.Result, typeof(BadRequestObjectResult),
                "An unparseable JSON Schema must be rejected at define time (400), not stored");
            // Nothing must have been persisted.
            Assert.IsNull(await store.GetSchema("bad.schema.v1"), "A rejected schema must not be stored");
        }

        [TestMethod]
        public async Task PostAgentEventTypes_sessionKeyPath_not_starting_with_dollar_returns_400()
        {
            var (impl, store) = Build();
            var req = new DefineEventTypeRequest
            {
                EventTypeId = "crm.contact.v1",
                JsonSchema = "{\"type\":\"object\"}",
                SessionKeyPath = "contactId", // missing leading '$'
            };

            var result = await impl.PostAgentEventTypesAsync(req);

            Assert.IsInstanceOfType(result.Result, typeof(BadRequestObjectResult),
                "A sessionKeyPath that is not a JSONPath (no leading '$') must yield 400");
            Assert.IsNull(await store.GetSchema("crm.contact.v1"), "A rejected schema must not be stored");
        }

        // ── PostAgentSubscribeAsync ──────────────────────────────────────────

        [TestMethod]
        public async Task PostAgentSubscribe_valid_returns_200_and_records_subscription()
        {
            var (impl, _, _, _, registry) = BuildAgent();

            var result = await impl.PostAgentSubscribeAsync(new AgentSubscribeRequest
            {
                EventTypeId = "crm.lead.v1",
            });

            Assert.IsInstanceOfType(result, typeof(OkResult), "Valid subscribe must yield 200");
            // CurrentAgentId() falls back to "demo-agent" with no X-Agent-Id header.
            CollectionAssert.Contains(registry.GetSubscriptions("demo-agent").ToArray(), "crm.lead.v1",
                "Registry must record the subscription for the current agent");
        }

        [TestMethod]
        public async Task PostAgentSubscribe_empty_eventTypeId_returns_400()
        {
            var (impl, _, _, _, registry) = BuildAgent();

            var result = await impl.PostAgentSubscribeAsync(new AgentSubscribeRequest { EventTypeId = "" });

            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult), "Empty eventTypeId must yield 400");
            Assert.AreEqual(0, registry.GetSubscriptions("demo-agent").Count, "Nothing must be recorded on a 400");
        }

        // ── GetAgentReceiveAsync ─────────────────────────────────────────────

        [TestMethod]
        public async Task GetAgentReceive_parked_handoff_returns_200_with_coordinates()
        {
            var (impl, store) = Build();
            await SeedPendingHandoff(store,
                eventId: "evt-1", sessionId: "sess-1", eventTypeId: "crm.lead.v1",
                payload: "{\"industry\":\"retail\"}",
                messageId: "msg-99", correlationId: "corr-77", originatingMessageId: "orig-55");

            var result = await impl.GetAgentReceiveAsync("crm.lead.v1", waitSeconds: 0);

            var ok = result.Result as OkObjectResult;
            Assert.IsNotNull(ok, "Expected 200 OkObjectResult");
            var msg = ok!.Value as AgentReceivedMessage;
            Assert.IsNotNull(msg);
            Assert.AreEqual("crm.lead.v1", msg!.EventTypeId);
            Assert.AreEqual("{\"industry\":\"retail\"}", msg.Payload);
            Assert.IsNotNull(msg.Coordinates);
            Assert.AreEqual("evt-1", msg.Coordinates.EventId);
            Assert.AreEqual("sess-1", msg.Coordinates.SessionId);
            Assert.AreEqual("msg-99", msg.Coordinates.MessageId);
            Assert.AreEqual("crm.lead.v1", msg.Coordinates.EventTypeId);
            Assert.AreEqual("corr-77", msg.Coordinates.CorrelationId);
            Assert.AreEqual("orig-55", msg.Coordinates.OriginatingMessageId);
        }

        [TestMethod]
        public async Task GetAgentReceive_empty_store_waitSeconds0_returns_204()
        {
            var (impl, _) = Build();

            var result = await impl.GetAgentReceiveAsync("crm.lead.v1", waitSeconds: 0);

            Assert.IsInstanceOfType(result.Result, typeof(NoContentResult), "Nothing parked must yield 204");
        }

        [TestMethod]
        public async Task GetAgentReceive_store_returns_null_pending_returns_204_not_500()
        {
            // The Cosmos store returns null (not empty) when the zone container is missing.
            // Receive must null-guard and report 204, never NRE into a 500.
            var store = new NullPendingStore();
            var impl = new AgentImplementation(
                store,
                new FakePlatform(),
                new CapturingPublisher(),
                store,
                new CapturingManagerClient(),
                new AgentSubscriptionRegistry(),
                config: null,
                httpContextAccessor: null,
                NullLogger<AgentImplementation>.Instance);

            var result = await impl.GetAgentReceiveAsync("crm.lead.v1", waitSeconds: 0);

            Assert.IsInstanceOfType(result.Result, typeof(NoContentResult),
                "A null pending result (zone container missing) must be guarded -> 204");
        }

        [TestMethod]
        public async Task GetAgentReceive_nonmatching_type_returns_204_matching_returns_200()
        {
            var (impl, store) = Build();
            await SeedPendingHandoff(store, "evt-1", "sess-1", "crm.lead.v1", "{}");

            var miss = await impl.GetAgentReceiveAsync("other.type.v1", waitSeconds: 0);
            Assert.IsInstanceOfType(miss.Result, typeof(NoContentResult),
                "A parked event of a different type than the query param must yield 204");

            var hit = await impl.GetAgentReceiveAsync("crm.lead.v1", waitSeconds: 0);
            Assert.IsInstanceOfType(hit.Result, typeof(OkObjectResult),
                "A parked event matching the query param must yield 200");
        }

        [TestMethod]
        public async Task GetAgentReceive_no_query_param_uses_subscriptions_to_filter()
        {
            var (impl, store, _, _, _) = BuildAgent();
            await SeedPendingHandoff(store, "evt-1", "sess-1", "crm.lead.v1", "{}");

            // Subscribed only to a different type -> the parked event must NOT match.
            await impl.PostAgentSubscribeAsync(new AgentSubscribeRequest { EventTypeId = "other.type.v1" });
            var miss = await impl.GetAgentReceiveAsync(eventTypeId: null, waitSeconds: 0);
            Assert.IsInstanceOfType(miss.Result, typeof(NoContentResult),
                "With a non-matching subscription and no query param, nothing matches -> 204");

            // Now subscribe to the parked type -> it matches.
            await impl.PostAgentSubscribeAsync(new AgentSubscribeRequest { EventTypeId = "crm.lead.v1" });
            var hit = await impl.GetAgentReceiveAsync(eventTypeId: null, waitSeconds: 0);
            Assert.IsInstanceOfType(hit.Result, typeof(OkObjectResult),
                "Once subscribed to the parked type, the event matches -> 200");
        }

        // ── PostAgentSettleAsync ─────────────────────────────────────────────

        private static AgentSettleRequest SettleRequest(
            string eventId,
            AgentSettleRequestOutcome outcome,
            string? result = null,
            string? errorText = null,
            string? errorType = null,
            string messageId = "msg-99")
        {
            return new AgentSettleRequest
            {
                Coordinates = new HandoffCoordinates { EventId = eventId, MessageId = messageId },
                Outcome = outcome,
                Result = result,
                ErrorText = errorText,
                ErrorType = errorType,
            };
        }

        [TestMethod]
        public async Task PostAgentSettle_complete_returns_200_and_calls_CompleteHandoff()
        {
            var (impl, store, _, manager, _) = BuildAgent();
            await SeedPendingHandoff(store, "evt-1", "sess-1", "crm.lead.v1", "{}", messageId: "msg-99");

            var result = await impl.PostAgentSettleAsync(
                SettleRequest("evt-1", AgentSettleRequestOutcome.Complete, result: "{\"importedId\":42}"));

            Assert.IsInstanceOfType(result, typeof(OkResult), "Complete settle must yield 200");
            Assert.AreEqual(1, manager.CompleteCount, "CompleteHandoff must be called once");
            Assert.AreEqual(0, manager.FailCount, "FailHandoff must NOT be called");
            Assert.AreEqual("evt-1", manager.CompletedEntry?.EventId);
            Assert.AreEqual("msg-99", manager.CompletedEntry?.MessageId, "MessageId comes from the request coordinates");
            Assert.AreEqual(ZoneId, manager.CompletedEndpoint, "Endpoint must be the zone id");
            Assert.AreEqual("{\"importedId\":42}", manager.CompletedDetails, "Result flows through as details");
        }

        [TestMethod]
        public async Task PostAgentSettle_fail_returns_200_and_calls_FailHandoff()
        {
            var (impl, store, _, manager, _) = BuildAgent();
            await SeedPendingHandoff(store, "evt-1", "sess-1", "crm.lead.v1", "{}");

            var result = await impl.PostAgentSettleAsync(
                SettleRequest("evt-1", AgentSettleRequestOutcome.Fail, errorText: "boom", errorType: "RuntimeError"));

            Assert.IsInstanceOfType(result, typeof(OkResult), "Fail settle must yield 200");
            Assert.AreEqual(1, manager.FailCount, "FailHandoff must be called once");
            Assert.AreEqual(0, manager.CompleteCount, "CompleteHandoff must NOT be called");
            Assert.AreEqual("evt-1", manager.FailedEntry?.EventId);
            Assert.AreEqual(ZoneId, manager.FailedEndpoint);
            Assert.AreEqual("boom", manager.FailedErrorText);
            Assert.AreEqual("RuntimeError", manager.FailedErrorType);
        }

        [TestMethod]
        public async Task PostAgentSettle_fail_without_errorText_returns_400()
        {
            var (impl, store, _, manager, _) = BuildAgent();
            await SeedPendingHandoff(store, "evt-1", "sess-1", "crm.lead.v1", "{}");

            var result = await impl.PostAgentSettleAsync(
                SettleRequest("evt-1", AgentSettleRequestOutcome.Fail, errorText: null));

            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult), "Missing errorText must yield 400");
            Assert.AreEqual(0, manager.FailCount, "FailHandoff must NOT be called without errorText");
        }

        [TestMethod]
        public async Task PostAgentSettle_event_not_found_returns_404()
        {
            var (impl, _, _, manager, _) = BuildAgent();

            var result = await impl.PostAgentSettleAsync(
                SettleRequest("missing-evt", AgentSettleRequestOutcome.Complete));

            Assert.IsInstanceOfType(result, typeof(NotFoundObjectResult), "Unknown event must yield 404");
            Assert.AreEqual(0, manager.CompleteCount);
        }

        [TestMethod]
        public async Task PostAgentSettle_event_not_pending_handoff_returns_400()
        {
            var (impl, store, _, manager, _) = BuildAgent();
            // Completed (not a pending handoff) event under the zone.
            await store.UploadCompletedMessage("evt-1", "sess-1", ZoneId, new UnresolvedEvent
            {
                EventTypeId = "crm.lead.v1",
                LastMessageId = "msg-1",
            });

            var result = await impl.PostAgentSettleAsync(
                SettleRequest("evt-1", AgentSettleRequestOutcome.Complete));

            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult), "Non-pending-handoff event must yield 400");
            Assert.AreEqual(0, manager.CompleteCount);
        }

        // ── PostAgentPublishAsync ────────────────────────────────────────────

        private const string IndustrySchema =
            "{\"type\":\"object\",\"required\":[\"industry\"],\"properties\":{\"industry\":{\"type\":\"string\"}}}";

        private static async Task SeedSchema(InMemoryMessageStore store, string eventTypeId, string jsonSchema, string? sessionKeyPath = null)
        {
            await store.DefineEventType(new NimBus.MessageStore.States.EventSchema
            {
                EventTypeId = eventTypeId,
                Name = eventTypeId,
                JsonSchema = jsonSchema,
                SessionKeyPath = sessionKeyPath,
                Version = 1,
                AgentId = "test",
                CreatedUtc = DateTime.UtcNow,
            });
        }

        [TestMethod]
        public async Task PostAgentPublish_unknown_eventTypeId_returns_404_and_does_not_publish()
        {
            var (impl, _, publisher) = BuildWithPublisher();

            var result = await impl.PostAgentPublishAsync(new AgentPublishRequest
            {
                EventTypeId = "never.defined.v1",
                Payload = "{\"industry\":\"retail\"}",
            });

            Assert.IsInstanceOfType(result, typeof(NotFoundObjectResult),
                "Unknown eventTypeId must yield 404");
            Assert.AreEqual(0, publisher.CallCount, "Publisher must NOT be called for an unknown event type");
        }

        [TestMethod]
        public async Task PostAgentPublish_payload_violates_schema_returns_400_and_does_not_publish()
        {
            var (impl, store, publisher) = BuildWithPublisher();
            await SeedSchema(store, "crm.lead.v1", IndustrySchema);

            // Missing the required "industry" property -> schema violation.
            var result = await impl.PostAgentPublishAsync(new AgentPublishRequest
            {
                EventTypeId = "crm.lead.v1",
                Payload = "{\"foo\":1}",
            });

            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "Schema-violating payload must yield 400");
            Assert.AreEqual(0, publisher.CallCount, "Publisher must NOT be called for an invalid payload");
        }

        [TestMethod]
        public async Task PostAgentPublish_malformed_json_payload_returns_400_and_does_not_publish()
        {
            var (impl, store, publisher) = BuildWithPublisher();
            await SeedSchema(store, "crm.lead.v1", IndustrySchema);

            // Unparseable JSON.
            var result = await impl.PostAgentPublishAsync(new AgentPublishRequest
            {
                EventTypeId = "crm.lead.v1",
                Payload = "{\"a\":",
            });

            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "Malformed JSON payload must yield 400");
            Assert.AreEqual(0, publisher.CallCount, "Publisher must NOT be called for malformed JSON");
        }

        [TestMethod]
        public async Task PostAgentPublish_valid_payload_returns_200_and_publishes_message()
        {
            var (impl, store, publisher) = BuildWithPublisher();
            await SeedSchema(store, "crm.lead.v1", IndustrySchema);

            const string payload = "{\"industry\":\"retail\"}";
            const string sessionId = "session-abc";

            var result = await impl.PostAgentPublishAsync(new AgentPublishRequest
            {
                EventTypeId = "crm.lead.v1",
                Payload = payload,
                SessionId = sessionId,
            });

            Assert.IsInstanceOfType(result, typeof(OkResult), "Valid payload must yield 200");
            Assert.AreEqual(1, publisher.CallCount, "Publisher must be called exactly once");

            var message = publisher.Published;
            Assert.IsNotNull(message, "A Message must be captured");
            Assert.AreEqual("crm.lead.v1", message!.EventTypeId);
            Assert.AreEqual("crm.lead.v1", message.To);
            Assert.AreEqual(MessageType.EventRequest, message.MessageType);
            Assert.AreEqual(sessionId, message.SessionId, "Provided sessionId must be carried through");
            Assert.IsNotNull(message.MessageContent?.EventContent);
            Assert.AreEqual("crm.lead.v1", message.MessageContent!.EventContent!.EventTypeId);
            Assert.AreEqual(payload, message.MessageContent.EventContent.EventJson);
        }

        [TestMethod]
        public async Task PostAgentPublish_valid_payload_without_sessionId_generates_one()
        {
            var (impl, store, publisher) = BuildWithPublisher();
            await SeedSchema(store, "crm.lead.v1", IndustrySchema);

            var result = await impl.PostAgentPublishAsync(new AgentPublishRequest
            {
                EventTypeId = "crm.lead.v1",
                Payload = "{\"industry\":\"retail\"}",
            });

            Assert.IsInstanceOfType(result, typeof(OkResult));
            Assert.IsFalse(string.IsNullOrWhiteSpace(publisher.Published?.SessionId),
                "A sessionId must be generated when none is supplied");
        }

        // ── PostAgentPublishAsync — sessionKeyPath derivation (finding 4) ────

        // Schema with an optional (not required) contactId used to derive the session key.
        private const string ContactSchema =
            "{\"type\":\"object\",\"properties\":{\"contactId\":{\"type\":\"string\"}}}";

        [TestMethod]
        public async Task PostAgentPublish_derives_sessionId_from_sessionKeyPath_and_is_stable()
        {
            var (impl, store, publisher) = BuildWithPublisher();
            await SeedSchema(store, "crm.contact.v1", ContactSchema, sessionKeyPath: "$.contactId");

            AgentPublishRequest Req() => new AgentPublishRequest
            {
                EventTypeId = "crm.contact.v1",
                Payload = "{\"contactId\":\"c-1\"}",
            };

            var first = await impl.PostAgentPublishAsync(Req());
            var firstSession = publisher.Published?.SessionId;
            var second = await impl.PostAgentPublishAsync(Req());
            var secondSession = publisher.Published?.SessionId;

            Assert.IsInstanceOfType(first, typeof(OkResult));
            Assert.IsInstanceOfType(second, typeof(OkResult));
            Assert.AreEqual("c-1", firstSession, "sessionId must be derived from the payload via sessionKeyPath");
            Assert.AreEqual(firstSession, secondSession,
                "Same business key must map to the same session so ordering is preserved");
        }

        [TestMethod]
        public async Task PostAgentPublish_explicit_sessionId_overrides_sessionKeyPath()
        {
            var (impl, store, publisher) = BuildWithPublisher();
            await SeedSchema(store, "crm.contact.v1", ContactSchema, sessionKeyPath: "$.contactId");

            var result = await impl.PostAgentPublishAsync(new AgentPublishRequest
            {
                EventTypeId = "crm.contact.v1",
                Payload = "{\"contactId\":\"c-1\"}",
                SessionId = "explicit-session",
            });

            Assert.IsInstanceOfType(result, typeof(OkResult));
            Assert.AreEqual("explicit-session", publisher.Published?.SessionId,
                "An explicit sessionId must win over sessionKeyPath derivation");
        }

        [TestMethod]
        public async Task PostAgentPublish_missing_sessionKey_value_falls_back_to_random_session()
        {
            var (impl, store, publisher) = BuildWithPublisher();
            // Schema declares a sessionKeyPath but does NOT require the field, so a payload
            // without it is schema-valid yet has no session key -> GUID fallback (unordered).
            await SeedSchema(store, "crm.contact.v1", ContactSchema, sessionKeyPath: "$.contactId");

            AgentPublishRequest Req() => new AgentPublishRequest
            {
                EventTypeId = "crm.contact.v1",
                Payload = "{\"note\":\"no contact id here\"}",
            };

            var first = await impl.PostAgentPublishAsync(Req());
            var firstSession = publisher.Published?.SessionId;
            var second = await impl.PostAgentPublishAsync(Req());
            var secondSession = publisher.Published?.SessionId;

            Assert.IsInstanceOfType(first, typeof(OkResult), "A missing key must degrade to unordered, not fail");
            Assert.IsTrue(Guid.TryParse(firstSession, out _), "The fallback session must be a fresh GUID");
            Assert.AreNotEqual(firstSession, secondSession,
                "Without a key each publish gets its own random session");
        }

        [TestMethod]
        public async Task PostAgentPublish_blank_eventTypeId_returns_400_and_does_not_publish()
        {
            var (impl, _, publisher) = BuildWithPublisher();

            var result = await impl.PostAgentPublishAsync(new AgentPublishRequest
            {
                EventTypeId = "   ",
                Payload = "{\"industry\":\"retail\"}",
            });

            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "Blank/whitespace eventTypeId must yield 400");
            Assert.AreEqual(0, publisher.CallCount, "Publisher must NOT be called when eventTypeId is blank");
        }

        [TestMethod]
        public async Task PostAgentPublish_blank_payload_returns_400_and_does_not_publish()
        {
            var (impl, _, publisher) = BuildWithPublisher();

            var result = await impl.PostAgentPublishAsync(new AgentPublishRequest
            {
                EventTypeId = "crm.lead.v1",
                Payload = "   ",
            });

            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "Blank/whitespace payload must yield 400");
            Assert.AreEqual(0, publisher.CallCount, "Publisher must NOT be called when payload is blank");
        }
    }
}
