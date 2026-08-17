#pragma warning disable CA1707, CA2007
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.Heartbeat;

namespace NimBus.WebApp.Tests;

[TestClass]
public sealed class HeartbeatPageApiTests
{
    [TestMethod]
    public async Task GetHeartbeatPageAsync_denies_users_without_site_reader()
    {
        var sut = new HeartbeatImplementation(new StubHeartbeatService(), [], new StubAuthorizationService(false));

        var response = await sut.GetHeartbeatPageAsync(30);

        Assert.IsInstanceOfType<ForbidResult>(response.Result);
    }

    [TestMethod]
    public async Task GetHeartbeatPageAsync_builds_weighted_metrics_and_exact_gap_duration()
    {
        var now = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
        var dayUtc = now.Date;
        var history = new InMemoryMessageStore();
        await history.UpsertHeartbeatUptimeDays([
            Day("alive", dayUtc, expected: 4, received: 4, observed: 1200),
            Day("missing", dayUtc, expected: 4, received: 2, observed: 1200),
        ]);
        await history.UpsertHeartbeatGaps([new HeartbeatGap
        {
            Id = $"missing|{now.AddHours(-1):O}",
            EndpointId = "missing",
            FromUtc = now.AddHours(-1),
            ToUtc = now,
            SdkVersionBefore = "1.0.0",
            SdkVersionAfter = "2.0.0",
        }]);
        var heartbeat = new StubHeartbeatService([
            Overview("alive", HeartbeatStatus.Unsupported, "1.2.3"),
            Overview("missing", HeartbeatStatus.Off, "1.0.0"),
            new HeartbeatOverviewItem { EndpointId = "excluded", IsHeartbeatEnabled = false },
        ]);
        var sut = new HeartbeatImplementation(
            heartbeat,
            [history],
            new StubAuthorizationService(true),
            new FixedTimeProvider(new DateTimeOffset(now)));

        var response = await sut.GetHeartbeatPageAsync(120);
        var page = response.Value;

        Assert.IsNotNull(page);
        Assert.AreEqual(90, page.WindowDays);
        Assert.AreEqual(2, page.AdaptersTotal);
        Assert.AreEqual(1, page.AdaptersReporting);
        Assert.AreEqual(0.75, page.FleetUptime!.Value, 0.001);
        CollectionAssert.Contains(page.AdaptersNeedingAttention, "missing");
        Assert.AreEqual(3600, page.Gaps.Single().DurationSeconds, 2,
            "Closed gap duration must not add another schedule interval.");
        Assert.AreEqual("redeployed", page.Gaps.Single().Cause);
        Assert.AreEqual("alive", page.Adapters.Single(row => row.EndpointId == "alive").Liveness);
        Assert.AreEqual("partial", page.Adapters.Single(row => row.EndpointId == "alive").Days.Last().State);
    }

    [TestMethod]
    public async Task GetHeartbeatPageAsync_compares_current_day_coverage_with_elapsed_time()
    {
        var now = new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
        var history = new InMemoryMessageStore();
        await history.UpsertHeartbeatUptimeDays([
            Day("alive", now.UtcDateTime.Date, expected: 144, received: 144, observed: 43_200),
        ]);
        var sut = new HeartbeatImplementation(
            new StubHeartbeatService([Overview("alive", HeartbeatStatus.On, "1.2.3")]),
            [history],
            new StubAuthorizationService(true),
            new FixedTimeProvider(now));

        var page = (await sut.GetHeartbeatPageAsync(1)).Value;

        Assert.IsNotNull(page);
        var today = page.Adapters.Single().Days.Single();
        Assert.AreEqual("full", today.State);
        Assert.AreEqual(1.0, today.Coverage, 0.001);
    }

    [TestMethod]
    public async Task GetHeartbeatPageAsync_resolves_case_variant_day_rows_deterministically()
    {
        var now = new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
        var history = new InMemoryMessageStore();
        var canonical = Day("Orders", now.UtcDateTime.Date, expected: 144, received: 144, observed: 43_200);
        canonical.LastBeatUtc = now.UtcDateTime;
        var duplicate = Day("orders", now.UtcDateTime.Date, expected: 144, received: 0, observed: 43_200);
        duplicate.LastBeatUtc = now.UtcDateTime.AddMinutes(1);
        await history.UpsertHeartbeatUptimeDays([canonical, duplicate]);
        var sut = new HeartbeatImplementation(
            new StubHeartbeatService([Overview("Orders", HeartbeatStatus.On, "1.2.3")]),
            [history],
            new StubAuthorizationService(true),
            new FixedTimeProvider(now));

        var page = (await sut.GetHeartbeatPageAsync(1)).Value;

        Assert.IsNotNull(page);
        Assert.AreEqual(1.0, page.FleetUptime!.Value, 0.001,
            "Exact platform casing wins over a newer case-variant Cosmos row.");
        Assert.AreEqual("full", page.Adapters.Single().Days.Single().State);
    }

    private static HeartbeatUptimeDay Day(string endpointId, DateTime dayUtc, int expected, int received, int observed)
        => new()
        {
            Id = $"{endpointId}|{dayUtc:yyyy-MM-dd}",
            EndpointId = endpointId,
            DayUtc = dayUtc,
            Expected = expected,
            Received = received,
            Missed = expected - received,
            ObservedSeconds = observed,
            LastBeatUtc = dayUtc.AddMinutes(expected),
        };

    private static HeartbeatOverviewItem Overview(string endpointId, HeartbeatStatus status, string version)
        => new()
        {
            EndpointId = endpointId,
            Status = status,
            LastStartTime = DateTime.UtcNow,
            SdkVersion = version,
        };

    private sealed class StubHeartbeatService(IReadOnlyList<HeartbeatOverviewItem>? overview = null) : IHeartbeatService
    {
        public Task<IReadOnlyList<HeartbeatOverviewItem>> GetOverviewAsync()
            => Task.FromResult(overview ?? []);

        public Task<HeartbeatSettings> GetSettingsAsync() => throw new NotSupportedException();
        public Task<HeartbeatSettings> SetSettingsAsync(HeartbeatSettings settings) => throw new NotSupportedException();
        public Task<int> SweepTimeoutsAsync() => throw new NotSupportedException();
        public Task<int> SendHeartbeatsAsync(bool force = false) => throw new NotSupportedException();
        public Task SetEndpointEnabledAsync(string endpointId, bool enabled) => throw new NotSupportedException();
        public Task<IReadOnlyList<ServiceHealth>> GetServiceHealthAsync() => throw new NotSupportedException();
        public Task<bool> ProbeResolverAsync() => throw new NotSupportedException();
        public Task<bool> RunScheduledTickAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubAuthorizationService(bool allowed) : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(allowed);
        public Task<bool> CanReadPiiAsync() => Task.FromResult(false);
        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => throw new NotSupportedException();
        public string? GetCurrentUserName() => null;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
