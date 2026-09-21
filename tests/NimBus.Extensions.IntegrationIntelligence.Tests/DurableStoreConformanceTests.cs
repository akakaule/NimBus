#pragma warning disable CA1707, CA2007
using Microsoft.Azure.Cosmos;
using Microsoft.Data.SqlClient;
using NimBus.Extensions.IntegrationIntelligence.Storage;

namespace NimBus.Extensions.IntegrationIntelligence.Tests;

[TestClass]
public sealed class DurableStoreConformanceTests
{
    [TestMethod]
    [DataRow("memory")]
    [DataRow("sql")]
    [DataRow("cosmos")]
    public async Task Independent_Stores_Conform_For_Races_Expiry_Fencing_Replay_And_Deletion(string provider)
    {
        await using var fixture = await StoreFixture.CreateAsync(provider);
        var firstStore = fixture.First;
        var secondStore = fixture.Second;
        var id = Guid.NewGuid().ToString("D");
        var scope = new ClassificationScope(id, "event", "endpoint", "session");
        var now = fixture.Clock.Now;
        var firstKey = Guid.NewGuid().ToString("D");
        var reservations = await Task.WhenAll(
            Task.Run(() => firstStore.ReserveAsync(id, firstKey, false, now, scope)),
            Task.Run(() => secondStore.ReserveAsync(id, Guid.NewGuid().ToString("D"), false, now, scope)));
        Assert.AreEqual(1, reservations.Count(r => r.IsOwner));
        Assert.AreEqual(reservations[0].OperationId, reservations[1].OperationId);
        var owner = reservations.Single(r => r.IsOwner);
        fixture.Clock.Now = now.AddSeconds(61);
        var expired = await secondStore.ReserveAsync(id, Guid.NewGuid().ToString("D"), false, fixture.Clock.Now, scope);
        Assert.IsFalse(expired.IsOwner);
        Assert.AreEqual("AnalysisOutcomeUnknown", expired.ErrorCode);
        var forceKey = Guid.NewGuid().ToString("D");
        var forced = await secondStore.ReserveAsync(id, forceKey, true, fixture.Clock.Now, scope);
        Assert.IsTrue(forced.IsOwner);
        Assert.AreEqual(2, forced.Revision);
        await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() => firstStore.CompleteAsync(owner.OperationId, Result(id, owner.Revision)));
        await firstStore.FailAsync(owner, "StaleFailure");
        Assert.AreEqual("Active", (await secondStore.GetLatestOperationAsync(id))!.Status);
        var result = Result(id, forced.Revision);
        await secondStore.CompleteAsync(forced.OperationId, result);
        await firstStore.CompleteAsync(forced.OperationId, result);
        await firstStore.FailAsync(forced, "LateFailure");
        var replay = await firstStore.ReserveAsync(id, forceKey, true, fixture.Clock.Now, scope);
        Assert.IsFalse(replay.IsOwner);
        Assert.AreEqual(result.Id, replay.CachedResult!.Id);
        await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() => firstStore.ReserveAsync(id, forceKey, false, fixture.Clock.Now, scope));
        Assert.HasCount(1, await secondStore.GetHistoryAsync(id));
        var next = await secondStore.ReserveAsync(id, Guid.NewGuid().ToString("D"), true, fixture.Clock.Now, scope);
        Assert.AreEqual(3, next.Revision);
        var retention = (IClassificationRetentionStore)firstStore;
        var candidates = new List<ClassificationScope>();
        await foreach (var candidate in retention.GetRetentionCandidatesAsync()) candidates.Add(candidate);
        CollectionAssert.Contains(candidates, scope);
        await retention.DeleteFailureAsync(id);
        await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() => secondStore.CompleteAsync(next.OperationId, Result(id, next.Revision)));
        Assert.HasCount(0, await firstStore.GetHistoryAsync(id));
        await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() => firstStore.ReserveAsync(id, Guid.NewGuid().ToString("D"), true, fixture.Clock.Now, scope));
    }

    internal static FailureClassification Result(string id, int revision) => new()
    {
        Id = $"{id}:{revision}", FailureMessageId = id, Revision = revision, EventId = "event", EventTypeId = "type", EndpointId = "endpoint",
        Provider = "TypeSafe", Model = "test", QuestionSetVersion = 1, Category = "unknown", CategoryConfidence = 1,
        CategoryProbabilities = new Dictionary<string, double> { ["unknown"] = 1 }, RetryLikelihood = 0,
        ChangeRequiredLikelihood = 0, ExternalDependencyLikelihood = 0, Guidance = FailureGuidance.Uncertain,
        EventPayloadIncluded = false, RequestedBy = "operator", CreatedAtUtc = DateTimeOffset.UtcNow,
    };
}

internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class StoreFixture : IAsyncDisposable
{
    public TestClock Clock { get; } = new();
    public IFailureClassificationStore First { get; private set; } = null!;
    public IFailureClassificationStore Second { get; private set; } = null!;
    private CosmosClient? _cosmos;
    private string? _database;
    private string? _sql;

    public static async Task<StoreFixture> CreateAsync(string provider)
    {
        var fixture = new StoreFixture();
        if (provider == "memory")
        {
            fixture.First = fixture.Second = new InMemoryClassificationStore(fixture.Clock);
            return fixture;
        }
        fixture._database = "IntelligenceTest_" + Guid.NewGuid().ToString("N");
        if (provider == "sql")
        {
            fixture._sql = Environment.GetEnvironmentVariable("NIMBUS_SQL_TEST_CONNECTION");
            if (string.IsNullOrWhiteSpace(fixture._sql)) Assert.Inconclusive("Set NIMBUS_SQL_TEST_CONNECTION to run SQL conformance.");
            await using var connection = new SqlConnection(fixture._sql);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // The identifier is generated here, never derived from input.
            command.CommandText = $"CREATE DATABASE [{fixture._database}]";
            await command.ExecuteNonQueryAsync();
            var builder = new SqlConnectionStringBuilder(fixture._sql) { InitialCatalog = fixture._database };
            var store = new SqlClassificationStore(builder.ConnectionString, fixture.Clock);
            await store.EnsureSchemaAsync();
            await store.EnsureSchemaAsync();
            fixture.First = store;
            fixture.Second = new SqlClassificationStore(builder.ConnectionString, fixture.Clock);
        }
        else
        {
            var connection = Environment.GetEnvironmentVariable("NIMBUS_COSMOS_TEST_CONNECTION");
            if (string.IsNullOrWhiteSpace(connection))
            {
                if (Environment.GetEnvironmentVariable("NIMBUS_COSMOS_TEST_REQUIRED") is "1" or "true") Assert.Fail("Cosmos conformance is required.");
                Assert.Inconclusive("Set NIMBUS_COSMOS_TEST_CONNECTION to run Cosmos conformance.");
            }
            fixture._cosmos = new CosmosClient(connection, new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway, LimitToEndpoint = true });
            var database = await fixture._cosmos.CreateDatabaseAsync(fixture._database);
            await database.Database.CreateContainerAsync("failureclassifications", "/failureMessageId");
            fixture.First = new CosmosClassificationStore(fixture._cosmos, fixture._database, clock: fixture.Clock);
            fixture.Second = new CosmosClassificationStore(fixture._cosmos, fixture._database, clock: fixture.Clock);
        }
        return fixture;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cosmos is not null)
        {
            await _cosmos.GetDatabase(_database).DeleteAsync();
            _cosmos.Dispose();
        }
        if (_sql is not null)
        {
            SqlConnection.ClearAllPools();
            await using var connection = new SqlConnection(_sql);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE [{_database}]";
            await command.ExecuteNonQueryAsync();
        }
    }
}
