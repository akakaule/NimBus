#pragma warning disable CA1707, CA2007
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Tests;

[TestClass]
public class MappingImplementationTests
{
    // The JSON schema seeded in each test. The real hash must be used when testing
    // approve so that SchemaHash.Of(schema.JsonSchema) == mapping.SourceSchemaHash.
    private const string SeedSchema = "{\"type\":\"object\"}";
    private static readonly string RealSchemaHash = SchemaHash.Of(SeedSchema);

    private static (MappingImplementation Impl, InMemoryMessageStore Store) Build()
    {
        var store = new InMemoryMessageStore();
        var impl = new MappingImplementation(store, store, NullLogger<MappingImplementation>.Instance);
        return (impl, store);
    }

    private static async Task SeedEventType(InMemoryMessageStore store, string id)
        => await store.DefineEventType(new EventSchema
        {
            EventTypeId = id,
            Name = id,
            JsonSchema = SeedSchema,
            Version = 1,
            AgentId = "t",
            CreatedUtc = DateTime.UtcNow,
        });

    /// <summary>
    /// Returns a valid <see cref="ProposeMappingRequest"/> where SourceSchemaHash is the
    /// REAL SHA-256 of <see cref="SeedSchema"/> so that approve succeeds without drift.
    /// </summary>
    private static ProposeMappingRequest Req(
        string src = "marketing.lead.created.v1",
        string tgt = "erp.customer.upsert.v1")
        => new ProposeMappingRequest
        {
            SourceEventTypeId = src,
            TargetEventTypeId = tgt,
            Transform = "{ \"customerId\": leadId }",
            SourceSchemaHash = RealSchemaHash,
        };

    [TestMethod]
    public async Task Propose_unknown_source_or_target_returns_404()
    {
        var (impl, _) = Build();
        var result = await impl.PostAgentMappingsAsync(Req());
        Assert.IsInstanceOfType(result.Result, typeof(NotFoundObjectResult));
    }

    [TestMethod]
    public async Task Propose_valid_returns_200_Draft()
    {
        var (impl, store) = Build();
        await SeedEventType(store, "marketing.lead.created.v1");
        await SeedEventType(store, "erp.customer.upsert.v1");

        var result = await impl.PostAgentMappingsAsync(Req());

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok, "Expected OkObjectResult (200)");
        var info = ok!.Value as MappingInfo;
        Assert.IsNotNull(info);
        Assert.AreEqual("marketing.lead.created.v1->erp.customer.upsert.v1", info!.Id);
        Assert.AreEqual(MappingInfoState.Draft, info.State);
        Assert.AreEqual(1, info.Version);
    }

    [TestMethod]
    public async Task Approve_transitions_Draft_to_Active()
    {
        var (impl, store) = Build();
        await SeedEventType(store, "marketing.lead.created.v1");
        await SeedEventType(store, "erp.customer.upsert.v1");

        // Propose with the real hash so approval won't hit the drift path.
        var info = ((await impl.PostAgentMappingsAsync(Req())).Result as OkObjectResult)!.Value as MappingInfo;
        Assert.IsNotNull(info, "Propose must succeed");

        var approve = await impl.PostAgentMappingApproveAsync(info!.Id);
        Assert.IsInstanceOfType(approve, typeof(OkResult), "Approve must yield 200");

        var active = await store.GetActiveMappingForSource("marketing.lead.created.v1");
        Assert.IsNotNull(active, "Approved mapping must become the Active mapping for its source");
    }

    [TestMethod]
    public async Task Approve_unknown_returns_404()
    {
        var (impl, _) = Build();
        var result = await impl.PostAgentMappingApproveAsync("nope->nope");
        Assert.IsInstanceOfType(result, typeof(NotFoundObjectResult));
    }
}
