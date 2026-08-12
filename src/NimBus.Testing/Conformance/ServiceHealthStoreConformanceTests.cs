#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Testing.Conformance;

/// <summary>
/// Provider-agnostic conformance suite for <see cref="IServiceHealthStore"/>.
/// A probe is in flight exactly while <see cref="ServiceHealth.LastProbeMessageId"/>
/// is set, and <see cref="ServiceHealth.Status"/> only ever holds a settled outcome.
/// </summary>
[TestClass]
public abstract class ServiceHealthStoreConformanceTests
{
    private readonly string _scope = $"ct-{Guid.NewGuid():N}"[..16];

    protected abstract IServiceHealthStore CreateStore();

    private string Id(string value) => $"{_scope}-{value}";

    [TestMethod]
    public async Task TryClaimServiceProbe_creates_the_row_on_first_use()
    {
        var store = CreateStore();
        var serviceId = Id("svc-new");

        Assert.IsTrue(await store.TryClaimServiceProbe(serviceId, DateTime.UtcNow.AddMinutes(5), "probe-1"));

        var health = await Row(store, serviceId);
        Assert.AreEqual("probe-1", health.LastProbeMessageId);
        Assert.IsNotNull(health.LastProbeSentUtc);
        // Nothing has settled yet, so the status stays Unknown rather than Pending.
        Assert.AreEqual(HeartbeatStatus.Unknown, health.Status);
    }

    [TestMethod]
    public async Task TryClaimServiceProbe_lets_exactly_one_caller_send_per_interval()
    {
        var store = CreateStore();
        var serviceId = Id("svc-claim");

        // Wide windows (±5 min) so the assertions hold whatever the store's own clock
        // reads relative to this process.
        Assert.IsTrue(await store.TryClaimServiceProbe(serviceId, DateTime.UtcNow.AddMinutes(5), "probe-1"));
        Assert.IsFalse(await store.TryClaimServiceProbe(serviceId, DateTime.UtcNow.AddMinutes(-5), "probe-2"),
            "The previous probe was just sent, so the next one is not due yet.");
        Assert.AreEqual("probe-1", (await Row(store, serviceId)).LastProbeMessageId);

        Assert.IsTrue(await store.TryClaimServiceProbe(serviceId, DateTime.UtcNow.AddMinutes(5), "probe-3"),
            "Once the interval has elapsed the claim is available again.");
        Assert.AreEqual("probe-3", (await Row(store, serviceId)).LastProbeMessageId);
    }

    [TestMethod]
    public async Task SetServiceHealth_settles_the_probe_and_clears_the_claim()
    {
        var store = CreateStore();
        var serviceId = Id("svc-settle");
        await store.TryClaimServiceProbe(serviceId, DateTime.UtcNow.AddMinutes(5), "probe-1");
        var sentAt = (await Row(store, serviceId)).LastProbeSentUtc;
        var seenAt = new DateTime(2026, 07, 10, 12, 0, 0, DateTimeKind.Utc);

        Assert.IsTrue(await store.SetServiceHealth(new ServiceHealth
        {
            ServiceId = serviceId,
            Status = HeartbeatStatus.On,
            Version = "1.2.3",
            LastSeenUtc = seenAt,
            RoundTripMs = 42,
        }));

        var settled = await Row(store, serviceId);
        Assert.AreEqual(HeartbeatStatus.On, settled.Status);
        Assert.AreEqual("1.2.3", settled.Version);
        Assert.AreEqual(seenAt.Ticks, settled.LastSeenUtc!.Value.Ticks);
        Assert.AreEqual(42L, settled.RoundTripMs!.Value);
        Assert.IsNull(settled.LastProbeMessageId);
        // The claim owns LastProbeSentUtc: an answer must not reset the send schedule.
        Assert.AreEqual(sentAt!.Value.Ticks, settled.LastProbeSentUtc!.Value.Ticks);
    }

    [TestMethod]
    public async Task SetServiceHealth_creates_the_row_when_an_answer_arrives_before_any_claim()
    {
        var store = CreateStore();
        var serviceId = Id("svc-unclaimed");

        Assert.IsTrue(await store.SetServiceHealth(new ServiceHealth
        {
            ServiceId = serviceId,
            Status = HeartbeatStatus.On,
            Version = "1.0.0",
        }));

        var health = await Row(store, serviceId);
        Assert.AreEqual(HeartbeatStatus.On, health.Status);
        Assert.IsNull(health.LastProbeMessageId);
        Assert.IsNull(health.LastProbeSentUtc);
    }

    [TestMethod]
    public async Task SweepTimedOutServiceProbes_settles_only_probes_sent_at_or_before_the_cutoff()
    {
        var store = CreateStore();
        var stale = Id("svc-stale");
        var fresh = Id("svc-fresh");
        await store.TryClaimServiceProbe(stale, DateTime.UtcNow.AddMinutes(5), "probe-stale");
        await store.TryClaimServiceProbe(fresh, DateTime.UtcNow.AddMinutes(5), "probe-fresh");

        var swept = await store.SweepTimedOutServiceProbes(DateTime.UtcNow.AddMinutes(-5));
        CollectionAssert.DoesNotContain(swept, stale, "A probe sent after the cutoff has not timed out.");
        CollectionAssert.DoesNotContain(swept, fresh);

        swept = await store.SweepTimedOutServiceProbes(DateTime.UtcNow.AddMinutes(5));

        CollectionAssert.Contains(swept, stale);
        var settled = await Row(store, stale);
        Assert.AreEqual(HeartbeatStatus.Off, settled.Status);
        Assert.IsNull(settled.LastProbeMessageId);

        // A settled row is no longer in flight, so a second sweep leaves it alone.
        var again = await store.SweepTimedOutServiceProbes(DateTime.UtcNow.AddMinutes(5));
        CollectionAssert.DoesNotContain(again, stale);
    }

    [TestMethod]
    public async Task GetServiceHealth_orders_by_service_id()
    {
        var store = CreateStore();
        var first = Id("svc-a");
        var second = Id("svc-b");
        await store.TryClaimServiceProbe(second, DateTime.UtcNow.AddMinutes(5), "probe-b");
        await store.TryClaimServiceProbe(first, DateTime.UtcNow.AddMinutes(5), "probe-a");

        var ids = (await store.GetServiceHealth()).Select(s => s.ServiceId).ToList();

        Assert.IsTrue(ids.IndexOf(first) >= 0 && ids.IndexOf(second) >= 0);
        Assert.IsTrue(ids.IndexOf(first) < ids.IndexOf(second),
            "Rows must come back ordered by service id, not by insertion order.");
    }

    private static async Task<ServiceHealth> Row(IServiceHealthStore store, string serviceId)
    {
        var rows = await store.GetServiceHealth();
        var row = rows.SingleOrDefault(s => string.Equals(s.ServiceId, serviceId, StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(row, $"Service health should carry a row for '{serviceId}'.");
        return row!;
    }
}
