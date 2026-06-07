#pragma warning disable CA1707, CA2007
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Transform;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;

namespace NimBus.MappingExecutor.Tests;

[TestClass]
public class MappingExecutorHandlerTests
{
    private const string Src = "marketing.lead.created.v1";
    private const string Tgt = "erp.customer.upsert.v1";
    private const string SourceSchema = "{\"type\":\"object\"}";

    private static async Task<(MappingExecutorHandler Handler, CapturingPublisher Pub, CapturingPark Park)> Build(
        InMemoryMessageStore store, MappingState state, string transform, string targetSchema)
    {
        await store.DefineEventType(new EventSchema { EventTypeId = Src, Name = Src, JsonSchema = SourceSchema, Version = 1, AgentId = "t", CreatedUtc = DateTime.UtcNow });
        await store.DefineEventType(new EventSchema { EventTypeId = Tgt, Name = Tgt, JsonSchema = targetSchema, Version = 1, AgentId = "t", CreatedUtc = DateTime.UtcNow });
        await store.SaveMapping(new EventMapping
        {
            Id = $"{Src}->{Tgt}", SourceEventTypeId = Src, TargetEventTypeId = Tgt,
            Transform = transform, SourceSchemaHash = SchemaHash.Of(SourceSchema),
            State = state, Version = 1,
        });
        var pub = new CapturingPublisher();
        var park = new CapturingPark();
        var handler = new MappingExecutorHandler(store, store, new JsonataTransformEngine(), pub, park, NullLogger<MappingExecutorHandler>.Instance);
        return (handler, pub, park);
    }

    [TestMethod]
    public async Task Active_mapping_transforms_validates_and_publishes_target()
    {
        var store = new InMemoryMessageStore();
        var (handler, pub, park) = await Build(store, MappingState.Active,
            transform: "{ \"customerId\": leadId }",
            targetSchema: "{\"type\":\"object\",\"required\":[\"customerId\"],\"properties\":{\"customerId\":{\"type\":\"string\"}}}");

        await handler.Handle(MessageContextStub.ForEventType(Src, "{ \"leadId\": \"L-1\" }"));

        Assert.AreEqual(1, pub.Count);
        Assert.AreEqual(Tgt, pub.LastEventTypeId);
        Assert.AreEqual(0, park.Count, "A valid mapping must not park");
    }

    [TestMethod]
    public async Task Output_failing_target_schema_parks_and_does_not_publish()
    {
        var store = new InMemoryMessageStore();
        var (handler, pub, park) = await Build(store, MappingState.Active,
            transform: "{ \"wrong\": leadId }",
            targetSchema: "{\"type\":\"object\",\"required\":[\"customerId\"],\"properties\":{\"customerId\":{\"type\":\"string\"}}}");

        await handler.Handle(MessageContextStub.ForEventType(Src, "{ \"leadId\": \"L-1\" }"));

        Assert.AreEqual(0, pub.Count, "Invalid output must not publish");
        Assert.AreEqual(1, park.Count, "Invalid output must park for recovery");
    }

    [TestMethod]
    public async Task Paused_mapping_parks_and_does_not_publish()
    {
        var store = new InMemoryMessageStore();
        var (handler, pub, park) = await Build(store, MappingState.Paused,
            transform: "{ \"customerId\": leadId }", targetSchema: "{\"type\":\"object\"}");

        await handler.Handle(MessageContextStub.ForEventType(Src, "{ \"leadId\": \"L-1\" }"));

        Assert.AreEqual(0, pub.Count);
        Assert.AreEqual(1, park.Count, "Paused mapping must park, not transform");
    }

    [TestMethod]
    public async Task No_mapping_parks_as_misconfiguration()
    {
        var store = new InMemoryMessageStore();
        var pub = new CapturingPublisher();
        var park = new CapturingPark();
        var handler = new MappingExecutorHandler(store, store, new JsonataTransformEngine(), pub, park, NullLogger<MappingExecutorHandler>.Instance);

        await handler.Handle(MessageContextStub.ForEventType("unrouted.type.v1", "{}"));

        Assert.AreEqual(0, pub.Count);
        Assert.AreEqual(1, park.Count, "A message with no mapping at the zone must park, not silently complete");
    }
}
