#pragma warning disable CA1707, CA2007
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
    private static readonly DateTime T0 = new(2026, 07, 10, 12, 0, 0, DateTimeKind.Utc);

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

    [TestMethod]
    public async Task GetMetadatas_includes_heartbeat_rows()
    {
        var store = CreateStore();
        var endpointId = Id("batch-heartbeats");
        await store.SetHeartbeat(
            Probe("probe-1", HeartbeatStatus.On, T0, T0.AddMilliseconds(50), "1.2.3"),
            endpointId);

        var filtered = await store.GetMetadatas([endpointId]);
        var fromAll = (await store.GetMetadatas()).Single(metadata => metadata.EndpointId == endpointId);

        Assert.IsNotNull(filtered);
        Assert.AreEqual(1, filtered!.Single().Heartbeats.Count,
            "The scheduled history fold uses the filtered batch overload and requires its probe rows.");
        Assert.AreEqual("probe-1", filtered.Single().Heartbeats.Single().MessageId);
        Assert.AreEqual(1, fromAll.Heartbeats.Count,
            "Both batch overloads must return complete EndpointMetadata records.");
    }

    // ───────── Heartbeat ─────────

    [TestMethod]
    public async Task SetHeartbeat_settles_the_pending_row_instead_of_duplicating_it()
    {
        var store = CreateStore();
        var endpointId = Id("hb-settle");

        await store.SetHeartbeat(Probe("probe-1", HeartbeatStatus.Pending, T0), endpointId);

        var afterSend = await store.GetEndpointMetadata(endpointId);
        Assert.AreEqual(1, afterSend.Heartbeats.Count);
        Assert.AreEqual(HeartbeatStatus.Pending, afterSend.EndpointHeartbeatStatus);

        // Same MessageId: the answer settles the row the send created.
        await store.SetHeartbeat(
            Probe("probe-1", HeartbeatStatus.On, T0, T0.AddMilliseconds(150), "9.9.9"),
            endpointId);

        var afterAnswer = await store.GetEndpointMetadata(endpointId);
        Assert.AreEqual(1, afterAnswer.Heartbeats.Count);
        var row = afterAnswer.Heartbeats[0];
        Assert.AreEqual(HeartbeatStatus.On, row.EndpointHeartbeatStatus);
        Assert.AreEqual(T0.Ticks, row.StartTime.Ticks);
        Assert.AreEqual(T0.AddMilliseconds(150).Ticks, row.EndTime.Ticks);
        Assert.AreEqual("9.9.9", row.SdkVersion);
        Assert.AreEqual(HeartbeatStatus.On, afterAnswer.EndpointHeartbeatStatus);
    }

    [TestMethod]
    public async Task SetHeartbeat_prunes_history_to_the_most_recent_rows()
    {
        var store = CreateStore();
        var endpointId = Id("hb-prune");
        const int written = HeartbeatRollup.MaxHeartbeatsPerEndpoint + 5;

        for (var i = 0; i < written; i++)
        {
            var start = T0.AddMinutes(i);
            await store.SetHeartbeat(
                Probe($"probe-{i:D2}", HeartbeatStatus.On, start, start.AddMilliseconds(10), "1.0.0"),
                endpointId);
        }

        var metadata = await store.GetEndpointMetadata(endpointId);
        Assert.AreEqual(HeartbeatRollup.MaxHeartbeatsPerEndpoint, metadata.Heartbeats.Count);
        // The oldest five were dropped, not the newest.
        Assert.AreEqual(T0.AddMinutes(5).Ticks, metadata.Heartbeats.Min(h => h.StartTime).Ticks);
        Assert.AreEqual(T0.AddMinutes(written - 1).Ticks, metadata.Heartbeats.Max(h => h.StartTime).Ticks);
    }

    [TestMethod]
    public async Task EnableHeartbeatOnEndpoint_drives_the_fan_out_filter()
    {
        var store = CreateStore();
        var optedIn = Id("hb-opt-in");
        var optedOut = Id("hb-opt-out");

        await store.EnableHeartbeatOnEndpoint(optedIn, true);
        await store.EnableHeartbeatOnEndpoint(optedOut, false);

        Assert.AreEqual(true, (await store.GetEndpointMetadata(optedIn)).IsHeartbeatEnabled);
        Assert.AreEqual(false, (await store.GetEndpointMetadata(optedOut)).IsHeartbeatEnabled);

        var enabled = (await store.GetMetadatasWithEnabledHeartbeat()).Select(m => m.EndpointId).ToList();
        CollectionAssert.Contains(enabled, optedIn);
        CollectionAssert.DoesNotContain(enabled, optedOut);

        await store.EnableHeartbeatOnEndpoint(optedIn, false);
        var afterOptOut = (await store.GetMetadatasWithEnabledHeartbeat()).Select(m => m.EndpointId).ToList();
        CollectionAssert.DoesNotContain(afterOptOut, optedIn);
    }

    [TestMethod]
    public async Task GetHeartbeatSettings_always_returns_the_singleton_record()
    {
        var store = CreateStore();

        var settings = await store.GetHeartbeatSettings();

        // Providers whose backing record has never been written report the documented
        // defaults; a shared backend may already carry an operator's edit, so only the
        // never-null contract and the shape are asserted here.
        Assert.IsNotNull(settings);
        Assert.AreEqual(HeartbeatSettings.SingletonId, settings.Id);
        Assert.IsTrue(settings.IntervalSeconds > 0);
        Assert.IsTrue(settings.TimeoutSeconds > 0);

        var defaults = new HeartbeatSettings();
        Assert.IsFalse(defaults.Enabled);
        Assert.AreEqual(300, defaults.IntervalSeconds);
        Assert.AreEqual(60, defaults.TimeoutSeconds);
    }

    [TestMethod]
    public async Task SetHeartbeatSettings_round_trips_and_keeps_LastSentAtUtc_when_none_is_supplied()
    {
        var store = CreateStore();
        var stamp = T0.AddHours(-3);

        Assert.IsTrue(await store.SetHeartbeatSettings(new HeartbeatSettings
        {
            Enabled = true,
            IntervalSeconds = 90,
            TimeoutSeconds = 45,
            LastSentAtUtc = stamp,
        }));

        var stored = await store.GetHeartbeatSettings();
        Assert.IsTrue(stored.Enabled);
        Assert.AreEqual(90, stored.IntervalSeconds);
        Assert.AreEqual(45, stored.TimeoutSeconds);
        Assert.AreEqual(stamp.Ticks, stored.LastSentAtUtc!.Value.Ticks);

        // The claim owns LastSentAtUtc: an operator edit must not reset the schedule.
        Assert.IsTrue(await store.SetHeartbeatSettings(new HeartbeatSettings
        {
            Enabled = false,
            IntervalSeconds = 120,
            TimeoutSeconds = 60,
            LastSentAtUtc = null,
        }));

        var preserved = await store.GetHeartbeatSettings();
        Assert.IsFalse(preserved.Enabled);
        Assert.AreEqual(120, preserved.IntervalSeconds);
        Assert.AreEqual(stamp.Ticks, preserved.LastSentAtUtc!.Value.Ticks);
    }

    [TestMethod]
    public async Task TryClaimHeartbeatSend_lets_exactly_one_caller_send_per_interval()
    {
        var store = CreateStore();
        await store.SetHeartbeatSettings(new HeartbeatSettings
        {
            Enabled = true,
            IntervalSeconds = 300,
            TimeoutSeconds = 60,
        });

        // Wide windows (±5 min) so the assertions hold whatever the store's own clock
        // reads relative to this process.
        Assert.IsTrue(await store.TryClaimHeartbeatSend(DateTime.UtcNow.AddMinutes(5)),
            "A send that is due must be claimable.");
        Assert.IsFalse(await store.TryClaimHeartbeatSend(DateTime.UtcNow.AddMinutes(-5)),
            "The claim just stamped a send, so the next one is not due yet.");
        Assert.IsTrue(await store.TryClaimHeartbeatSend(DateTime.UtcNow.AddMinutes(5)),
            "Once the interval has elapsed the claim is available again.");
    }

    [TestMethod]
    public async Task TryClaimHeartbeatSend_returns_false_while_heartbeats_are_disabled()
    {
        var store = CreateStore();
        await store.SetHeartbeatSettings(new HeartbeatSettings
        {
            Enabled = false,
            IntervalSeconds = 300,
            TimeoutSeconds = 60,
        });

        Assert.IsFalse(await store.TryClaimHeartbeatSend(DateTime.UtcNow.AddMinutes(5)));
    }

    [TestMethod]
    public async Task SweepTimedOutHeartbeats_settles_only_stale_pending_rows()
    {
        var store = CreateStore();
        var stale = Id("hb-stale");
        var fresh = Id("hb-fresh");
        var answered = Id("hb-answered");
        var now = DateTime.UtcNow;

        await store.SetHeartbeat(Probe("stale-1", HeartbeatStatus.Pending, now.AddMinutes(-10)), stale);
        await store.SetHeartbeat(Probe("fresh-1", HeartbeatStatus.Pending, now), fresh);
        await store.SetHeartbeat(
            Probe("answered-1", HeartbeatStatus.On, now.AddMinutes(-10), now.AddMinutes(-10).AddMilliseconds(20), "1.0.0"),
            answered);

        var swept = await store.SweepTimedOutHeartbeats(now.AddMinutes(-5));

        CollectionAssert.Contains(swept, stale);
        CollectionAssert.DoesNotContain(swept, fresh);
        CollectionAssert.DoesNotContain(swept, answered);
        Assert.AreEqual(HeartbeatStatus.Off, (await store.GetEndpointMetadata(stale)).EndpointHeartbeatStatus);
        Assert.AreEqual(HeartbeatStatus.Pending, (await store.GetEndpointMetadata(fresh)).EndpointHeartbeatStatus);
        Assert.AreEqual(HeartbeatStatus.On, (await store.GetEndpointMetadata(answered)).EndpointHeartbeatStatus);
    }

    [TestMethod]
    public async Task GetHeartbeatOverview_in_flight_probe_does_not_mask_the_last_settled_outcome()
    {
        var store = CreateStore();
        var endpointId = Id("ov-inflight");
        await store.SetHeartbeat(Probe("p1", HeartbeatStatus.On, T0, T0.AddMilliseconds(120), "10.0.5"), endpointId);
        await store.SetHeartbeat(Probe("p2", HeartbeatStatus.Pending, T0.AddMinutes(5)), endpointId);

        var item = await Overview(store, endpointId);

        Assert.AreEqual(HeartbeatStatus.On, item.Status);
        Assert.AreEqual(120L, item.RoundTripMs!.Value);
        Assert.AreEqual("10.0.5", item.SdkVersion);
        Assert.AreEqual("p2", item.MessageId);
        Assert.AreEqual(T0.AddMinutes(5).Ticks, item.LastStartTime!.Value.Ticks);
        Assert.AreEqual(T0.AddMilliseconds(120).Ticks, item.LastEndTime!.Value.Ticks);
    }

    [TestMethod]
    public async Task GetHeartbeatOverview_dead_endpoint_keeps_showing_off_while_the_next_probe_is_in_flight()
    {
        var store = CreateStore();
        var endpointId = Id("ov-dead");
        await store.SetHeartbeat(Probe("p1", HeartbeatStatus.Off, T0), endpointId);
        await store.SetHeartbeat(Probe("p2", HeartbeatStatus.Pending, T0.AddMinutes(5)), endpointId);

        Assert.AreEqual(HeartbeatStatus.Off, (await Overview(store, endpointId)).Status);
    }

    [TestMethod]
    public async Task GetHeartbeatOverview_first_probe_shows_pending_until_it_settles()
    {
        var store = CreateStore();
        var endpointId = Id("ov-first");
        await store.SetHeartbeat(Probe("p1", HeartbeatStatus.Pending, T0), endpointId);

        var item = await Overview(store, endpointId);

        Assert.AreEqual(HeartbeatStatus.Pending, item.Status);
        Assert.IsNull(item.RoundTripMs);
        Assert.IsNull(item.LastEndTime);
    }

    [TestMethod]
    public async Task GetHeartbeatOverview_endpoint_without_probes_reads_unknown()
    {
        var store = CreateStore();
        var endpointId = Id("ov-none");
        await store.SetEndpointMetadata(SampleMetadata(endpointId));

        var item = await Overview(store, endpointId);

        Assert.AreEqual(HeartbeatStatus.Unknown, item.Status);
        Assert.IsNull(item.LastStartTime);
        Assert.IsNull(item.RoundTripMs);
    }

    [TestMethod]
    public async Task GetHeartbeatOverview_round_trip_and_sdk_version_come_from_the_last_answered_probe()
    {
        var store = CreateStore();
        var endpointId = Id("ov-answered");
        // A swept (timed-out) Off row never carried a response; its timestamps must
        // not feed the round-trip column.
        await store.SetHeartbeat(Probe("p1", HeartbeatStatus.On, T0, T0.AddMilliseconds(80), "10.0.4"), endpointId);
        await store.SetHeartbeat(Probe("p2", HeartbeatStatus.Off, T0.AddMinutes(5)), endpointId);

        var item = await Overview(store, endpointId);

        Assert.AreEqual(HeartbeatStatus.Off, item.Status);
        Assert.AreEqual(80L, item.RoundTripMs!.Value);
        Assert.AreEqual("10.0.4", item.SdkVersion);
    }

    [TestMethod]
    public async Task Rollup_keeps_the_settled_outcome_when_the_next_probe_is_written_as_pending()
    {
        var store = CreateStore();
        var endpointId = Id("rollup-pending");
        await store.SetHeartbeat(Probe("p1", HeartbeatStatus.On, T0, T0.AddMilliseconds(120), "1.0.0"), endpointId);
        await store.SetHeartbeat(Probe("p2", HeartbeatStatus.Pending, T0.AddMinutes(5)), endpointId);

        Assert.AreEqual(HeartbeatStatus.On, (await store.GetEndpointMetadata(endpointId)).EndpointHeartbeatStatus);
    }

    [TestMethod]
    public async Task Rollup_follows_the_newest_settled_outcome()
    {
        var store = CreateStore();
        var endpointId = Id("rollup-off");
        await store.SetHeartbeat(Probe("p1", HeartbeatStatus.On, T0, T0.AddMilliseconds(120), "1.0.0"), endpointId);
        await store.SetHeartbeat(Probe("p2", HeartbeatStatus.Off, T0.AddMinutes(5)), endpointId);

        Assert.AreEqual(HeartbeatStatus.Off, (await store.GetEndpointMetadata(endpointId)).EndpointHeartbeatStatus);
    }

    private static async Task<HeartbeatOverviewItem> Overview(IEndpointMetadataStore store, string endpointId)
    {
        var overview = await store.GetHeartbeatOverview();
        var item = overview.SingleOrDefault(i => string.Equals(i.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(item, $"Overview should carry a row for '{endpointId}'.");
        return item!;
    }

    private static Heartbeat Probe(
        string messageId,
        HeartbeatStatus status,
        DateTime start,
        DateTime? end = null,
        string? sdkVersion = null) => new()
    {
        MessageId = messageId,
        StartTime = start,
        ReceivedTime = end ?? start,
        EndTime = end ?? start,
        SdkVersion = sdkVersion,
        EndpointHeartbeatStatus = status,
    };

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
