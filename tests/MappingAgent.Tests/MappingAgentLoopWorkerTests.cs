#pragma warning disable CA1707, CA2007

using System.Net;
using MappingAgent;
using MappingAgent.Authoring;
using MappingAgent.Bus;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MappingAgent.Tests;

/// <summary>
/// Tests for <see cref="MappingAgentLoopWorker.AuthorNextAsync"/>'s re-proposal guard: the agent
/// authors a mapping once and must not re-POST over a Draft/Active/Paused mapping every loop.
/// </summary>
[TestClass]
public class MappingAgentLoopWorkerTests
{
    private const string SourceSchema =
        "{\"type\":\"object\",\"required\":[\"leadId\",\"company\",\"email\"]," +
        "\"properties\":{\"leadId\":{\"type\":\"string\"}," +
        "\"company\":{\"type\":\"string\"},\"email\":{\"type\":\"string\"}}}";

    private const string TargetSchema =
        "{\"type\":\"object\",\"required\":[\"customerId\",\"companyName\",\"email\"]," +
        "\"properties\":{\"customerId\":{\"type\":\"string\"}," +
        "\"companyName\":{\"type\":\"string\"},\"email\":{\"type\":\"string\"}}}";

    private static MappingAgentLoopWorker Build(FakeGateway gateway)
        => new(gateway, new DeterministicMappingAuthor(), NullLogger<MappingAgentLoopWorker>.Instance);

    private static MappingSummary Existing(string state) => new(
        $"{MappingAgentLoopWorker.SourceEventTypeId}->{MappingAgentLoopWorker.TargetEventTypeId}",
        MappingAgentLoopWorker.SourceEventTypeId,
        MappingAgentLoopWorker.TargetEventTypeId,
        state);

    [TestMethod]
    public async Task AuthorNext_proposes_when_no_existing_mapping()
    {
        var gateway = new FakeGateway(SourceSchema, TargetSchema);
        var proposed = await Build(gateway).AuthorNextAsync(default);

        Assert.IsTrue(proposed);
        Assert.AreEqual(1, gateway.ProposeCalls, "A fresh source→target must be proposed exactly once.");
    }

    [TestMethod]
    public async Task AuthorNext_skips_when_existing_is_Draft()
    {
        var gateway = new FakeGateway(SourceSchema, TargetSchema) { Existing = { Existing("Draft") } };
        var proposed = await Build(gateway).AuthorNextAsync(default);

        Assert.IsTrue(proposed);
        Assert.AreEqual(0, gateway.ProposeCalls, "A Draft proposal already exists — must not re-propose.");
    }

    [TestMethod]
    public async Task AuthorNext_skips_when_existing_is_Active()
    {
        var gateway = new FakeGateway(SourceSchema, TargetSchema) { Existing = { Existing("Active") } };
        var proposed = await Build(gateway).AuthorNextAsync(default);

        Assert.IsTrue(proposed);
        Assert.AreEqual(0, gateway.ProposeCalls, "An operator-approved Active mapping must not be overwritten.");
    }

    [TestMethod]
    public async Task AuthorNext_skips_when_existing_is_Paused()
    {
        var gateway = new FakeGateway(SourceSchema, TargetSchema) { Existing = { Existing("Paused") } };
        var proposed = await Build(gateway).AuthorNextAsync(default);

        Assert.IsTrue(proposed);
        Assert.AreEqual(0, gateway.ProposeCalls, "A Paused mapping is operator-owned — must not re-propose.");
    }

    [TestMethod]
    public async Task AuthorNext_reproposes_when_existing_is_Rejected()
    {
        var gateway = new FakeGateway(SourceSchema, TargetSchema) { Existing = { Existing("Rejected") } };
        var proposed = await Build(gateway).AuthorNextAsync(default);

        Assert.IsTrue(proposed);
        Assert.AreEqual(1, gateway.ProposeCalls, "A Rejected mapping is re-authorable — must re-propose.");
    }

    [TestMethod]
    public async Task AuthorNext_reproposes_when_existing_is_Stale()
    {
        var gateway = new FakeGateway(SourceSchema, TargetSchema) { Existing = { Existing("Stale") } };
        var proposed = await Build(gateway).AuthorNextAsync(default);

        Assert.IsTrue(proposed);
        Assert.AreEqual(1, gateway.ProposeCalls, "A Stale (drifted) mapping is re-authorable — must re-propose.");
    }

    [TestMethod]
    public async Task AuthorNext_swallows_409_from_propose()
    {
        // Race: the list read saw no mapping but the POST 409s (operator approved concurrently).
        var gateway = new FakeGateway(SourceSchema, TargetSchema) { ThrowConflictOnPropose = true };

        // Must not throw — the worker treats a 409 as already-proposed.
        var proposed = await Build(gateway).AuthorNextAsync(default);

        Assert.IsTrue(proposed);
        Assert.AreEqual(1, gateway.ProposeCalls, "Propose was attempted once and its 409 was swallowed.");
    }

    /// <summary>In-memory <see cref="IMappingBusGateway"/> that records proposal calls.</summary>
    private sealed class FakeGateway : IMappingBusGateway
    {
        private readonly string _sourceSchema;
        private readonly string _targetSchema;

        public FakeGateway(string sourceSchema, string targetSchema)
        {
            _sourceSchema = sourceSchema;
            _targetSchema = targetSchema;
        }

        public List<MappingSummary> Existing { get; } = new();
        public int ProposeCalls { get; private set; }
        public bool ThrowConflictOnPropose { get; init; }

        public Task<IReadOnlyList<CatalogEntry>> GetCatalogAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CatalogEntry>>(new List<CatalogEntry>
            {
                new(MappingAgentLoopWorker.SourceEventTypeId, "source", _sourceSchema),
                new(MappingAgentLoopWorker.TargetEventTypeId, "target", _targetSchema),
            });

        public Task<IReadOnlyList<string>> GetSamplePayloadsAsync(
            string eventTypeId, int maxCount, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<IReadOnlyList<MappingSummary>> GetMappingsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MappingSummary>>(Existing);

        public Task<string> ProposeMappingAsync(
            string sourceEventTypeId,
            string targetEventTypeId,
            string transform,
            string rationale,
            string sourceSchemaHash,
            string? workedExamplesJson,
            CancellationToken ct = default)
        {
            ProposeCalls++;
            if (ThrowConflictOnPropose)
                throw new HttpRequestException("conflict", inner: null, statusCode: HttpStatusCode.Conflict);
            return Task.FromResult($"{sourceEventTypeId}->{targetEventTypeId}");
        }
    }
}
