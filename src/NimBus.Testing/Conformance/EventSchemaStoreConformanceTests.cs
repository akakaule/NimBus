#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Testing.Conformance;

/// <summary>
/// Provider-agnostic conformance suite for <see cref="IEventSchemaStore"/>.
/// </summary>
[TestClass]
public abstract class EventSchemaStoreConformanceTests
{
    protected abstract IEventSchemaStore CreateStore();

    private static EventSchema Sample(string id = "crm.contact.enriched.v1", string schema = "{\"type\":\"object\"}")
        => new EventSchema
        {
            EventTypeId = id,
            Name = "Contact Enriched",
            JsonSchema = schema,
            Description = "test",
            Version = 1,
            AgentId = "agent-1",
            CreatedBy = "agent-1",
            CreatedUtc = new DateTime(2026, 06, 05, 0, 0, 0, DateTimeKind.Utc),
        };

    [TestMethod]
    public async Task DefineEventType_then_GetSchema_round_trips()
    {
        var store = CreateStore();
        var id = $"ct.{Guid.NewGuid():N}.v1";
        await store.DefineEventType(Sample(id));
        var got = await store.GetSchema(id);
        Assert.IsNotNull(got);
        Assert.AreEqual(id, got.EventTypeId);
        Assert.AreEqual("{\"type\":\"object\"}", got.JsonSchema);
    }

    [TestMethod]
    public async Task GetSchema_unknown_returns_null()
    {
        var store = CreateStore();
        Assert.IsNull(await store.GetSchema($"ct.{Guid.NewGuid():N}.v1"));
    }

    [TestMethod]
    public async Task DefineEventType_identical_is_idempotent()
    {
        var store = CreateStore();
        var id = $"ct.{Guid.NewGuid():N}.v1";
        await store.DefineEventType(Sample(id));
        var second = await store.DefineEventType(Sample(id));
        Assert.AreEqual(id, second.EventTypeId);
        Assert.AreEqual(1, (await store.GetSchemas()).Count(s => s.EventTypeId == id));
    }

    [TestMethod]
    public async Task DefineEventType_changed_schema_throws_conflict()
    {
        var store = CreateStore();
        var id = $"ct.{Guid.NewGuid():N}.v1";
        await store.DefineEventType(Sample(id, "{\"type\":\"object\"}"));
        await Assert.ThrowsExceptionAsync<SchemaConflictException>(
            () => store.DefineEventType(Sample(id, "{\"type\":\"object\",\"required\":[\"x\"]}")));
    }

    [TestMethod]
    public async Task GetSchemas_returns_registered()
    {
        var store = CreateStore();
        var id = $"ct.{Guid.NewGuid():N}.v1";
        await store.DefineEventType(Sample(id));
        Assert.IsTrue((await store.GetSchemas()).Any(s => s.EventTypeId == id));
    }
}
