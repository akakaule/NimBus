#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Extensions.IntegrationIntelligence;
using NimBus.Extensions.IntegrationIntelligence.Evidence;
using NimBus.Extensions.IntegrationIntelligence.Storage;

namespace NimBus.Extensions.IntegrationIntelligence.Tests;

[TestClass]
public sealed class ContractsAndStoreTests
{
    private static readonly string[] QuestionIds = ["failure_category", "retry_likely_to_succeed_unchanged", "change_required_before_success", "external_dependency_involved"];
    [TestMethod]
    [DataRow(0.59, 0.90, 0.00, "transient_dependency", FailureGuidance.Uncertain)]
    [DataRow(0.60, 0.75, 0.00, "transient_dependency", FailureGuidance.RetryMayHelp)]
    [DataRow(0.90, 0.74, 0.00, "transient_dependency", FailureGuidance.Investigate)]
    [DataRow(0.90, 0.10, 0.90, "business_rule", FailureGuidance.ChangeLikelyRequired)]
    public void Guidance_Uses_The_Specified_Order(double confidence, double retry, double changeRequired, string category, FailureGuidance expected)
    {
        var options = new FailureClassificationOptions();
        var result = new FailureIntelligenceProviderResult(category, category, confidence, new Dictionary<string, double> { [category] = 1 }, retry, changeRequired, 0, null, null);

        Assert.AreEqual(expected, FailureGuidanceRules.Compose(result, options));
    }

    [TestMethod]
    public async Task InMemoryStore_Caches_NonForce_And_Allocates_Forced_Revisions()
    {
        var store = new InMemoryClassificationStore();
        var now = DateTimeOffset.UtcNow;
        var first = await store.ReserveAsync("failure-1", Guid.NewGuid().ToString("D"), false, now);
        Assert.IsTrue(first.IsOwner);
        var result = CreateResult(first.Revision);
        await store.CompleteAsync(first.OperationId, result);

        var cached = await store.ReserveAsync("failure-1", Guid.NewGuid().ToString("D"), false, now.AddSeconds(1));
        Assert.IsFalse(cached.IsOwner);
        Assert.AreEqual(result.Id, cached.CachedResult?.Id);

        var forced = await store.ReserveAsync("failure-1", Guid.NewGuid().ToString("D"), true, now.AddSeconds(2));
        Assert.IsTrue(forced.IsOwner);
        Assert.AreEqual(2, forced.Revision);
    }

    [TestMethod]
    public async Task InMemoryStore_Duplicate_Idempotency_Key_Returns_The_Same_Owner()
    {
        var store = new InMemoryClassificationStore();
        var key = Guid.NewGuid().ToString("D");
        var reservations = await Task.WhenAll(
            store.ReserveAsync("failure-2", key, false, DateTimeOffset.UtcNow),
            store.ReserveAsync("failure-2", key, false, DateTimeOffset.UtcNow));

        Assert.AreEqual(1, reservations.Count(reservation => reservation.IsOwner));
        Assert.AreEqual(reservations[0].OperationId, reservations[1].OperationId);
    }

    [TestMethod]
    public void SecretRedactor_Scrubs_Keys_And_FreeText_Secrets()
    {
        var redactor = new IntelligenceDataRedactor();

        StringAssert.Contains(redactor.ScrubJson("{\"password\":\"secret\",\"nested\":{\"token\":\"abc\"}}")!, "[redacted]", StringComparison.Ordinal);
        StringAssert.Contains(IntelligenceDataRedactor.ScrubText("Authorization: Bearer abc123", 100)!, "[redacted]", StringComparison.Ordinal);
        Assert.IsNull(IntelligenceDataRedactor.ScrubText("System.Exception: bad\n at Namespace.Handler.Run()", 100));
    }

    [TestMethod]
    public async Task Expired_Reservation_Requires_Explicit_Force_And_Fences_Old_Owner()
    {
        var store = new InMemoryClassificationStore();
        var now = DateTimeOffset.UtcNow;
        var first = await store.ReserveAsync("failure-1", Guid.NewGuid().ToString("D"), false, now.AddMinutes(-2));
        var unknown = await store.ReserveAsync("failure-1", Guid.NewGuid().ToString("D"), false, now);
        Assert.IsFalse(unknown.IsOwner, "Expiry must not replay paid work.");
        var next = await store.ReserveAsync("failure-1", Guid.NewGuid().ToString("D"), true, now);
        Assert.IsTrue(next.IsOwner);
        await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() => store.CompleteAsync(first.OperationId, CreateResult(first.Revision)));
        Assert.IsNull(await store.GetLatestAsync("failure-1"));
    }

    [TestMethod]
    public void Exception_Name_Alone_Is_Not_A_Stack_Dump()
    {
        const string text = "CustomerNotFoundException: Customer 4711 does not exist";
        Assert.AreEqual(text, IntelligenceDataRedactor.ScrubText(text, 100));
    }

    [TestMethod]
    public void QuestionSet_Contains_All_Version_One_Questions()
    {
        Assert.AreEqual(1, FailureClassificationQuestionSet.Version);
        CollectionAssert.AreEquivalent(
            QuestionIds,
            FailureClassificationQuestionSet.Items.Keys.ToArray());
        CollectionAssert.Contains(FailureClassificationQuestionSet.Items["failure_category"].Criteria!.Keys.ToArray(), "unknown");
    }

    private static FailureClassification CreateResult(int revision)
        => new()
        {
            Id = $"failure-1:{revision}", FailureMessageId = "failure-1", Revision = revision,
            EventId = "event-1", EventTypeId = "event.type", EndpointId = "endpoint-1",
            Provider = "TypeSafe", Model = "jev-1.13.0", QuestionSetVersion = 1,
            Category = "unknown", CategoryConfidence = 1, CategoryProbabilities = new Dictionary<string, double> { ["unknown"] = 1 },
            RetryLikelihood = 0, ChangeRequiredLikelihood = 0, ExternalDependencyLikelihood = 0,
            Guidance = FailureGuidance.Investigate, EventPayloadIncluded = false, RequestedBy = "operator", CreatedAtUtc = DateTimeOffset.UtcNow,
        };
}
