#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Testing.Conformance;

/// <summary>Provider-neutral contract for durable endpoint heartbeat history.</summary>
[TestClass]
public abstract class HeartbeatHistoryStoreConformanceTests
{
    private readonly string _scope = $"hh-{Guid.NewGuid():N}"[..16];

    protected abstract IHeartbeatHistoryStore CreateStore();

    [TestMethod]
    public async Task Uptime_days_round_trip_and_replace()
    {
        var store = CreateStore();
        var day = DateTime.UtcNow.Date.AddDays(-2);
        var row = Day("one", day, expected: 3);

        Assert.IsTrue(await store.UpsertHeartbeatUptimeDays([row]));
        row.Expected = 7;
        Assert.IsTrue(await store.UpsertHeartbeatUptimeDays([row]));

        var stored = (await store.GetHeartbeatUptimeDays(day)).Single(item => item.EndpointId == row.EndpointId);
        Assert.AreEqual(7, stored.Expected);
    }

    [TestMethod]
    public async Task Gap_query_returns_every_overlap_shape_and_replace()
    {
        var store = CreateStore();
        var from = DateTime.UtcNow.Date.AddDays(-7);
        var beforeInside = Gap("before-inside", from.AddDays(-2), from.AddHours(1));
        var inside = Gap("inside", from.AddHours(2), from.AddHours(3));
        var ongoing = Gap("ongoing", from.AddDays(-3), null);
        var before = Gap("before", from.AddDays(-4), from.AddDays(-1));
        await store.UpsertHeartbeatGaps([beforeInside, inside, ongoing, before]);

        inside.SdkVersionAfter = "2.0.0";
        Assert.IsTrue(await store.UpsertHeartbeatGaps([inside]));
        var rows = await store.GetHeartbeatGaps(from);

        CollectionAssert.AreEquivalent(
            new[] { beforeInside.EndpointId, inside.EndpointId, ongoing.EndpointId },
            rows.Select(row => row.EndpointId).Where(id => id.StartsWith(_scope, StringComparison.Ordinal)).ToArray());
        Assert.AreEqual("2.0.0", rows.Single(row => row.EndpointId == inside.EndpointId).SdkVersionAfter);
    }

    [TestMethod]
    public async Task Empty_upserts_succeed_without_rows()
    {
        var store = CreateStore();

        Assert.IsTrue(await store.UpsertHeartbeatUptimeDays([]));
        Assert.IsTrue(await store.UpsertHeartbeatGaps([]));
    }

    [TestMethod]
    public async Task Fold_claim_allows_one_caller_until_due()
    {
        var store = CreateStore();

        Assert.IsTrue(await store.TryClaimHeartbeatHistoryFold(DateTime.UtcNow.AddMinutes(1)));
        Assert.IsFalse(await store.TryClaimHeartbeatHistoryFold(DateTime.UtcNow.AddMinutes(-1)));
        Assert.IsTrue(await store.TryClaimHeartbeatHistoryFold(DateTime.UtcNow.AddMinutes(1)));
    }

    [TestMethod]
    public async Task Prune_removes_old_days_and_closed_gaps_but_keeps_open_gaps()
    {
        var store = CreateStore();
        var cutoff = DateTime.UtcNow.Date.AddDays(-3);
        var oldDay = Day("old-day", cutoff.AddDays(-1), 1);
        var freshDay = Day("fresh-day", cutoff, 1);
        var oldClosed = Gap("old-closed", cutoff.AddDays(-2), cutoff.AddSeconds(-1));
        var oldOpen = Gap("old-open", cutoff.AddDays(-2), null);
        await store.UpsertHeartbeatUptimeDays([oldDay, freshDay]);
        await store.UpsertHeartbeatGaps([oldClosed, oldOpen]);

        await store.PruneHeartbeatHistory(cutoff);

        var days = await store.GetHeartbeatUptimeDays(cutoff.AddDays(-10));
        Assert.IsFalse(days.Any(row => row.EndpointId == oldDay.EndpointId));
        Assert.IsTrue(days.Any(row => row.EndpointId == freshDay.EndpointId));
        var gaps = await store.GetHeartbeatGaps(cutoff.AddDays(-10));
        Assert.IsFalse(gaps.Any(row => row.EndpointId == oldClosed.EndpointId));
        Assert.IsTrue(gaps.Any(row => row.EndpointId == oldOpen.EndpointId));
    }

    private HeartbeatUptimeDay Day(string suffix, DateTime dayUtc, int expected)
        => new()
        {
            Id = $"{_scope}-{suffix}|{dayUtc:yyyy-MM-dd}",
            EndpointId = $"{_scope}-{suffix}",
            DayUtc = dayUtc,
            Expected = expected,
            Received = expected,
            ObservedSeconds = expected * 300,
            LastBeatUtc = dayUtc.AddMinutes(expected),
        };

    private HeartbeatGap Gap(string suffix, DateTime fromUtc, DateTime? toUtc)
        => new()
        {
            Id = $"{_scope}-{suffix}|{fromUtc:O}",
            EndpointId = $"{_scope}-{suffix}",
            FromUtc = fromUtc,
            ToUtc = toUtc,
            SdkVersionBefore = "1.0.0",
            SdkVersionAfter = string.Empty,
        };
}
