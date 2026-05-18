using System;
using System.Collections.Generic;
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

    private static EndpointMetadata SampleMetadata(string endpointId) => new()
    {
        EndpointId = endpointId,
        EndpointOwner = "Alice",
        EndpointOwnerTeam = "Team Blue",
        EndpointOwnerEmail = "owner@example.com",
        TechnicalContacts = new List<TechnicalContact>
        {
            new() { Name = "Ops", Email = "ops@example.com" },
        },
        SubscriptionStatus = true,
    };
}
