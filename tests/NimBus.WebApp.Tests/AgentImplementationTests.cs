#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;

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

        // ── Builder ─────────────────────────────────────────────────────────

        private static (AgentImplementation Impl, InMemoryMessageStore Store) Build(
            params string[] endpointIds)
        {
            var store = new InMemoryMessageStore();
            var platform = new FakePlatform(endpointIds);
            var impl = new AgentImplementation(
                store,
                platform,
                NullLogger<AgentImplementation>.Instance);
            return (impl, store);
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
            var req = ValidRequest(eventTypeId: "order.placed.v1", jsonSchema: "{\"type\":\"object\"}");

            var result = await impl.PostAgentEventTypesAsync(req);

            var ok = result.Result as OkObjectResult;
            Assert.IsNotNull(ok, "Expected OkObjectResult (200)");

            var info = ok!.Value as EventTypeInfo;
            Assert.IsNotNull(info);
            Assert.AreEqual("order.placed.v1", info!.EventTypeId);
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
        public async Task PostAgentPublish_stub_returns_501()
        {
            var (impl, _) = Build();
            var result = await impl.PostAgentPublishAsync(new AgentPublishRequest());
            var statusResult = result as StatusCodeResult;
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
    }
}
