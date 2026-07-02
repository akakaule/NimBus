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

    // ── Re-propose guards (finding 1) ─────────────────────────────────────────

    [TestMethod]
    public async Task Repropose_over_Active_returns_409_and_keeps_Active_with_ApprovedBy()
    {
        var (impl, store) = Build();
        await SeedEventType(store, "marketing.lead.created.v1");
        await SeedEventType(store, "erp.customer.upsert.v1");

        var info = ((await impl.PostAgentMappingsAsync(Req())).Result as OkObjectResult)!.Value as MappingInfo;
        var approve = await impl.PostAgentMappingApproveAsync(info!.Id);
        Assert.IsInstanceOfType(approve, typeof(OkResult), "Approve must yield 200");

        // Re-propose the same source→target: must be rejected, not silently demoted to Draft.
        var reproposed = await impl.PostAgentMappingsAsync(Req());
        Assert.IsInstanceOfType(reproposed.Result, typeof(ConflictObjectResult),
            "Re-proposing over an Active mapping must return 409 Conflict");

        var stored = await store.GetMapping(info.Id);
        Assert.IsNotNull(stored);
        Assert.AreEqual(MappingState.Active, stored!.State, "Existing mapping must remain Active");
        Assert.AreEqual("operator", stored.ApprovedBy, "ApprovedBy must remain intact");
    }

    [TestMethod]
    public async Task Repropose_over_Paused_returns_409()
    {
        var (impl, store) = Build();
        await SeedEventType(store, "marketing.lead.created.v1");
        await SeedEventType(store, "erp.customer.upsert.v1");

        var info = ((await impl.PostAgentMappingsAsync(Req())).Result as OkObjectResult)!.Value as MappingInfo;
        await impl.PostAgentMappingApproveAsync(info!.Id);
        var pause = await impl.PostAgentMappingPauseAsync(info.Id);
        Assert.IsInstanceOfType(pause, typeof(OkResult), "Pause must yield 200");

        var reproposed = await impl.PostAgentMappingsAsync(Req());
        Assert.IsInstanceOfType(reproposed.Result, typeof(ConflictObjectResult),
            "Re-proposing over a Paused mapping must return 409 Conflict");

        var stored = await store.GetMapping(info.Id);
        Assert.AreEqual(MappingState.Paused, stored!.State, "Existing mapping must remain Paused");
    }

    [TestMethod]
    public async Task Repropose_over_Rejected_is_allowed_new_Draft_version_bumped()
    {
        var (impl, store) = Build();
        await SeedEventType(store, "marketing.lead.created.v1");
        await SeedEventType(store, "erp.customer.upsert.v1");

        var info = ((await impl.PostAgentMappingsAsync(Req())).Result as OkObjectResult)!.Value as MappingInfo;
        var reject = await impl.PostAgentMappingRejectAsync(info!.Id);
        Assert.IsInstanceOfType(reject, typeof(OkResult), "Reject must yield 200");

        var reproposed = await impl.PostAgentMappingsAsync(Req());
        var ok = reproposed.Result as OkObjectResult;
        Assert.IsNotNull(ok, "Re-proposing over a Rejected mapping must be allowed (200)");
        var newInfo = ok!.Value as MappingInfo;
        Assert.AreEqual(MappingInfoState.Draft, newInfo!.State, "Re-proposal must be a fresh Draft");
        Assert.AreEqual(2, newInfo.Version, "Version must be bumped over the prior record");
    }

    [TestMethod]
    public async Task Repropose_over_Stale_is_allowed_new_Draft_version_bumped()
    {
        var (impl, store) = Build();
        await SeedEventType(store, "marketing.lead.created.v1");
        await SeedEventType(store, "erp.customer.upsert.v1");

        // Propose with a mismatched schema hash so approve marks it Stale (drift path).
        var driftReq = Req();
        driftReq.SourceSchemaHash = "sha256:deadbeef";
        var info = ((await impl.PostAgentMappingsAsync(driftReq)).Result as OkObjectResult)!.Value as MappingInfo;
        var approve = await impl.PostAgentMappingApproveAsync(info!.Id);
        Assert.IsInstanceOfType(approve, typeof(ConflictObjectResult), "Drifted approve marks Stale (409)");

        var stale = await store.GetMapping(info.Id);
        Assert.AreEqual(MappingState.Stale, stale!.State);

        var reproposed = await impl.PostAgentMappingsAsync(Req());
        var ok = reproposed.Result as OkObjectResult;
        Assert.IsNotNull(ok, "Re-proposing over a Stale mapping must be allowed (200)");
        var newInfo = ok!.Value as MappingInfo;
        Assert.AreEqual(MappingInfoState.Draft, newInfo!.State);
        Assert.AreEqual(2, newInfo.Version, "Version must be bumped over the prior record");
    }

    // ── Resume guard (finding 2) ──────────────────────────────────────────────

    [TestMethod]
    public async Task Resume_on_Paused_transitions_to_Active()
    {
        var (impl, store) = Build();
        await SeedEventType(store, "marketing.lead.created.v1");
        await SeedEventType(store, "erp.customer.upsert.v1");

        var info = ((await impl.PostAgentMappingsAsync(Req())).Result as OkObjectResult)!.Value as MappingInfo;
        await impl.PostAgentMappingApproveAsync(info!.Id);
        await impl.PostAgentMappingPauseAsync(info.Id);

        var resume = await impl.PostAgentMappingResumeAsync(info.Id);
        Assert.IsInstanceOfType(resume, typeof(OkResult), "Resume from Paused must yield 200");

        var active = await store.GetActiveMappingForSource("marketing.lead.created.v1");
        Assert.IsNotNull(active, "Resumed mapping must become the Active mapping again");
    }

    [TestMethod]
    public async Task Resume_on_Draft_returns_409_and_leaves_state_unchanged()
    {
        var (impl, store) = Build();
        await SeedEventType(store, "marketing.lead.created.v1");
        await SeedEventType(store, "erp.customer.upsert.v1");

        var info = ((await impl.PostAgentMappingsAsync(Req())).Result as OkObjectResult)!.Value as MappingInfo;

        var resume = await impl.PostAgentMappingResumeAsync(info!.Id);
        Assert.IsInstanceOfType(resume, typeof(ConflictObjectResult),
            "Resume must not activate a Draft (would bypass the approval gate)");

        var stored = await store.GetMapping(info.Id);
        Assert.AreEqual(MappingState.Draft, stored!.State, "State must be unchanged");
        Assert.IsNull(stored.ApprovedBy, "ApprovedBy must stay null — no approval granted");
    }

    [TestMethod]
    public async Task Resume_on_Rejected_returns_409()
    {
        var (impl, store) = Build();
        await SeedEventType(store, "marketing.lead.created.v1");
        await SeedEventType(store, "erp.customer.upsert.v1");

        var info = ((await impl.PostAgentMappingsAsync(Req())).Result as OkObjectResult)!.Value as MappingInfo;
        await impl.PostAgentMappingRejectAsync(info!.Id);

        var resume = await impl.PostAgentMappingResumeAsync(info.Id);
        Assert.IsInstanceOfType(resume, typeof(ConflictObjectResult),
            "Resume must not activate a Rejected mapping");

        var stored = await store.GetMapping(info.Id);
        Assert.AreEqual(MappingState.Rejected, stored!.State, "State must be unchanged");
    }

    [TestMethod]
    public async Task Pause_on_Draft_returns_409()
    {
        var (impl, store) = Build();
        await SeedEventType(store, "marketing.lead.created.v1");
        await SeedEventType(store, "erp.customer.upsert.v1");

        var info = ((await impl.PostAgentMappingsAsync(Req())).Result as OkObjectResult)!.Value as MappingInfo;

        var pause = await impl.PostAgentMappingPauseAsync(info!.Id);
        Assert.IsInstanceOfType(pause, typeof(ConflictObjectResult),
            "Pause must only apply to an Active mapping (a Draft→Paused→Resume path would bypass approval)");

        var stored = await store.GetMapping(info.Id);
        Assert.AreEqual(MappingState.Draft, stored!.State, "State must be unchanged");
    }
}
