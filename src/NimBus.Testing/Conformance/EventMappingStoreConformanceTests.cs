#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Testing.Conformance;

/// <summary>Provider-agnostic conformance suite for <see cref="IEventMappingStore"/>.</summary>
[TestClass]
public abstract class EventMappingStoreConformanceTests
{
    protected abstract IEventMappingStore CreateStore();

    private static EventMapping Sample(string source, string target = "erp.customer.upsert.v1", MappingState state = MappingState.Draft)
        => new EventMapping
        {
            Id = $"{source}->{target}",
            SourceEventTypeId = source,
            TargetEventTypeId = target,
            Transform = "{ \"id\": id }",
            SourceSchemaHash = "hash-1",
            State = state,
            Version = 1,
            CreatedBy = "agent-1",
            CreatedUtc = new DateTime(2026, 06, 07, 0, 0, 0, DateTimeKind.Utc),
        };

    [TestMethod]
    public async Task SaveMapping_then_GetMapping_round_trips()
    {
        var store = CreateStore();
        var src = $"src.{Guid.NewGuid():N}.v1";
        var saved = await store.SaveMapping(Sample(src));
        var got = await store.GetMapping(saved.Id);
        Assert.IsNotNull(got);
        Assert.AreEqual(src, got!.SourceEventTypeId);
        Assert.AreEqual(MappingState.Draft, got.State);
    }

    [TestMethod]
    public async Task GetMapping_unknown_returns_null()
    {
        var store = CreateStore();
        Assert.IsNull(await store.GetMapping($"x.{Guid.NewGuid():N}->y"));
    }

    [TestMethod]
    public async Task SaveMapping_upserts_by_id()
    {
        var store = CreateStore();
        var src = $"src.{Guid.NewGuid():N}.v1";
        await store.SaveMapping(Sample(src));
        var updated = Sample(src, state: MappingState.Active);
        await store.SaveMapping(updated);
        var got = await store.GetMapping(updated.Id);
        Assert.AreEqual(MappingState.Active, got!.State);
        Assert.AreEqual(1, (await store.GetMappings()).Count(m => m.Id == updated.Id));
    }

    [TestMethod]
    public async Task GetActiveMappingForSource_returns_only_active()
    {
        var store = CreateStore();
        var src = $"src.{Guid.NewGuid():N}.v1";
        await store.SaveMapping(Sample(src, state: MappingState.Draft));
        Assert.IsNull(await store.GetActiveMappingForSource(src), "Draft must not be returned as Active");

        await store.SaveMapping(Sample(src, state: MappingState.Active));
        var active = await store.GetActiveMappingForSource(src);
        Assert.IsNotNull(active);
        Assert.AreEqual(MappingState.Active, active!.State);
    }

    [TestMethod]
    public async Task GetMappings_returns_saved()
    {
        var store = CreateStore();
        var src = $"src.{Guid.NewGuid():N}.v1";
        await store.SaveMapping(Sample(src));
        Assert.IsTrue((await store.GetMappings()).Any(m => m.SourceEventTypeId == src));
    }
}
