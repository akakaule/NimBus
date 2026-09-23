#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Threading;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.WebApp.Services.Simulation;

namespace NimBus.WebApp.Tests.Simulation;

/// <summary>Shared fixtures for the simulation tests.</summary>
internal static class SimulationTestPlatform
{
    /// <summary>The bundled Storefront/Billing/Warehouse platform the WebApp serves by default.</summary>
    public static IPlatform Create() => new PlatformConfiguration();

    public const string Storefront = "StorefrontEndpoint";
    public const string Billing = "BillingEndpoint";
    public const string Warehouse = "WarehouseEndpoint";
}

/// <summary>A clock that only changes when a test moves it; timers still run in real time.</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Collects deliveries recorded by simulated handlers.</summary>
internal sealed class RecordingFeedSink : ISimulationFeedSink
{
    private readonly List<SimulatedDelivery> _deliveries = new();

    public IReadOnlyList<SimulatedDelivery> Deliveries
    {
        get { lock (_deliveries) { return _deliveries.ToArray(); } }
    }

    public void Record(SimulatedDelivery delivery)
    {
        lock (_deliveries) { _deliveries.Add(delivery); }
    }
}

/// <summary>Counts publish outcomes.</summary>
internal sealed class RecordingPublishSink : ISimulationPublishSink
{
    private int _published;
    private int _failed;
    private int _abandoned;
    private readonly Dictionary<string, int> _byEventType = new(StringComparer.Ordinal);

    public int PublishedCount => Volatile.Read(ref _published);
    public int FailedCount => Volatile.Read(ref _failed);
    public int AbandonedCount => Volatile.Read(ref _abandoned);

    public int PublishedFor(string eventTypeId)
    {
        lock (_byEventType) { return _byEventType.TryGetValue(eventTypeId, out var n) ? n : 0; }
    }

    public void Published(string endpointId, string eventTypeId)
    {
        Interlocked.Increment(ref _published);
        lock (_byEventType) { _byEventType[eventTypeId] = PublishedFor(eventTypeId) + 1; }
    }

    public void PublishFailed(string endpointId, string eventTypeId, Exception error) => Interlocked.Increment(ref _failed);

    public void SendsAbandoned(string endpointId, int count) => Interlocked.Add(ref _abandoned, count);
}
