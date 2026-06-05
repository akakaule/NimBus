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
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using IMessage = global::NimBus.Core.Messages.IMessage;
using MessageType = global::NimBus.Core.Messages.MessageType;

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

        // ── Builder ─────────────────────────────────────────────────────────

        private static (AgentImplementation Impl, InMemoryMessageStore Store) Build(
            params string[] endpointIds)
        {
            var (impl, store, _) = BuildWithPublisher(endpointIds);
            return (impl, store);
        }

        private static (AgentImplementation Impl, InMemoryMessageStore Store, CapturingPublisher Publisher) BuildWithPublisher(
            params string[] endpointIds)
        {
            var store = new InMemoryMessageStore();
            var platform = new FakePlatform(endpointIds);
            var publisher = new CapturingPublisher();
            var impl = new AgentImplementation(
                store,
                platform,
                publisher,
                NullLogger<AgentImplementation>.Instance);
            return (impl, store, publisher);
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

        // ── Stubs return 501 ─────────────────────────────────────────────────

        [TestMethod]
        public async Task PostAgentSubscribe_stub_returns_501()
        {
            var (impl, _) = Build();
            var result = await impl.PostAgentSubscribeAsync(new AgentSubscribeRequest());
            var statusResult = result as StatusCodeResult;
            Assert.AreEqual(501, statusResult?.StatusCode);
        }

        [TestMethod]
        public async Task GetAgentReceive_stub_returns_501()
        {
            var (impl, _) = Build();
            var result = await impl.GetAgentReceiveAsync("any.type", null);
            var statusResult = result.Result as StatusCodeResult;
            Assert.AreEqual(501, statusResult?.StatusCode);
        }

        [TestMethod]
        public async Task PostAgentSettle_stub_returns_501()
        {
            var (impl, _) = Build();
            var result = await impl.PostAgentSettleAsync(new AgentSettleRequest());
            var statusResult = result as StatusCodeResult;
            Assert.AreEqual(501, statusResult?.StatusCode);
        }

        // ── PostAgentPublishAsync ────────────────────────────────────────────

        private const string IndustrySchema =
            "{\"type\":\"object\",\"required\":[\"industry\"],\"properties\":{\"industry\":{\"type\":\"string\"}}}";

        private static async Task SeedSchema(InMemoryMessageStore store, string eventTypeId, string jsonSchema)
        {
            await store.DefineEventType(new NimBus.MessageStore.States.EventSchema
            {
                EventTypeId = eventTypeId,
                Name = eventTypeId,
                JsonSchema = jsonSchema,
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
