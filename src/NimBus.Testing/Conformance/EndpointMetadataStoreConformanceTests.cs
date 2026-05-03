using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Testing.Conformance;

/// <summary>
/// Provider-agnostic conformance suite for <see cref="IEndpointMetadataStore"/>.
/// </summary>
[TestClass]
public abstract class EndpointMetadataStoreConformanceTests
{
    private readonly string _scope = $"ct-{Guid.NewGuid():N}"[..16];

    protected abstract IEndpointMetadataStore CreateStore();

    private string Id(string value) => $"{_scope}-{value}";

    [TestMethod]
    public async Task SetEndpointMetadata_then_GetEndpointMetadata_round_trips()
    {
        var store = CreateStore();
        var endpointId = Id("ep-meta");
        var metadata = SampleMetadata(endpointId);

        var saved = await store.SetEndpointMetadata(metadata);
        Assert.IsTrue(saved);

        var fetched = await store.GetEndpointMetadata(endpointId);
        Assert.AreEqual(endpointId, fetched.EndpointId);
        Assert.AreEqual("Team Blue", fetched.EndpointOwnerTeam);
        Assert.AreEqual("owner@example.com", fetched.EndpointOwnerEmail);
        Assert.AreEqual(HeartbeatStatus.On, fetched.EndpointHeartbeatStatus);
        Assert.AreEqual(true, fetched.IsHeartbeatEnabled);
        Assert.AreEqual(true, fetched.SubscriptionStatus);
        Assert.AreEqual(1, fetched.TechnicalContacts.Count);
        Assert.AreEqual("Ops", fetched.TechnicalContacts[0].Name);
    }

    [TestMethod]
    public async Task GetMetadatas_filters_by_endpoint_ids()
    {
        var store = CreateStore();
        var endpointOne = Id("ep-one");
        var endpointTwo = Id("ep-two");
        await store.SetEndpointMetadata(SampleMetadata(endpointOne));
        await store.SetEndpointMetadata(SampleMetadata(endpointTwo));

        var all = await store.GetMetadatas();
        Assert.IsTrue(all.Count >= 2);

        var filtered = await store.GetMetadatas(new[] { endpointTwo, Id("missing") });
        Assert.IsNotNull(filtered);
        Assert.AreEqual(1, filtered!.Count);
        Assert.AreEqual(endpointTwo, filtered[0].EndpointId);
    }

    [TestMethod]
    public async Task EnableHeartbeatOnEndpoint_updates_enabled_heartbeat_listing()
    {
        var store = CreateStore();
        var endpointId = Id("ep-heartbeat");

        await store.EnableHeartbeatOnEndpoint(endpointId, enable: true);

        var metadata = await store.GetEndpointMetadata(endpointId);
        Assert.AreEqual(true, metadata.IsHeartbeatEnabled);

        var heartbeatEnabled = await store.GetMetadatasWithEnabledHeartbeat();
        Assert.IsTrue(heartbeatEnabled.Any(m => m.EndpointId == endpointId));
    }

    [TestMethod]
    public async Task SetHeartbeat_updates_endpoint_status_and_history()
    {
        var store = CreateStore();
        var endpointId = Id("ep-heartbeat-status");
        await store.SetEndpointMetadata(SampleMetadata(endpointId));

        var heartbeat = new Heartbeat
        {
            MessageId = "hb-1",
            StartTime = DateTime.UtcNow.AddSeconds(-3),
            ReceivedTime = DateTime.UtcNow.AddSeconds(-2),
            EndTime = DateTime.UtcNow.AddSeconds(-1),
            EndpointHeartbeatStatus = HeartbeatStatus.Off,
        };

        var saved = await store.SetHeartbeat(heartbeat, endpointId);
        Assert.IsTrue(saved);

        var metadata = await store.GetEndpointMetadata(endpointId);
        Assert.AreEqual(HeartbeatStatus.Off, metadata.EndpointHeartbeatStatus);
        Assert.IsNotNull(metadata.Heartbeats);
        Assert.IsTrue(metadata.Heartbeats.Any(h => h.MessageId == "hb-1" && h.EndpointHeartbeatStatus == HeartbeatStatus.Off));
    }

    private static EndpointMetadata SampleMetadata(string endpointId) => new()
    {
        EndpointId = endpointId,
        EndpointOwner = "Alice",
        EndpointOwnerTeam = "Team Blue",
        EndpointOwnerEmail = "owner@example.com",
        IsHeartbeatEnabled = true,
        EndpointHeartbeatStatus = HeartbeatStatus.On,
        TechnicalContacts = new List<TechnicalContact>
        {
            new() { Name = "Ops", Email = "ops@example.com" },
        },
        Heartbeats = new List<Heartbeat>(),
        SubscriptionStatus = true,
    };
}
