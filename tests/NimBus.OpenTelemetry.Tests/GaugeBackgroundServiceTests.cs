#pragma warning disable CA1707, CA2007
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Diagnostics;
using NimBus.Core.Outbox;
using NimBus.OpenTelemetry.Instrumentation;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace NimBus.OpenTelemetry.Tests;

[TestClass]
public sealed class GaugeBackgroundServiceTests
{
    [TestMethod]
    public async Task Outbox_pending_gauge_reports_cached_value()
    {
        var metrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(metrics)
            .Build()!;

        using var sut = new NimBusGaugeBackgroundService(
            new TestOptionsMonitor(new NimBusOpenTelemetryOptions()),
            outboxQuery: new FakeOutboxMetricsQuery { PendingCount = 42 });
        await sut.PollNowAsync();
        meterProvider.ForceFlush();

        var gauge = metrics.Single(m => m.Name == "nimbus.outbox.pending");
        Assert.AreEqual(42, ReadLatestLong(gauge));
    }

    [TestMethod]
    public async Task Outbox_dispatch_lag_reports_seconds_since_oldest()
    {
        var metrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(metrics)
            .Build()!;

        using var sut = new NimBusGaugeBackgroundService(
            new TestOptionsMonitor(new NimBusOpenTelemetryOptions()),
            outboxQuery: new FakeOutboxMetricsQuery { OldestPending = DateTimeOffset.UtcNow.AddSeconds(-90) });
        await sut.PollNowAsync();
        meterProvider.ForceFlush();

        var lag = ReadLatestLong(metrics.Single(m => m.Name == "nimbus.outbox.dispatch_lag"));
        Assert.IsTrue(lag >= 88 && lag <= 95, $"Expected ~90s lag, got {lag}");
    }

    [TestMethod]
    public async Task Outbox_dispatch_lag_is_zero_when_no_pending()
    {
        var metrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(metrics)
            .Build()!;

        using var sut = new NimBusGaugeBackgroundService(
            new TestOptionsMonitor(new NimBusOpenTelemetryOptions()),
            outboxQuery: new FakeOutboxMetricsQuery { OldestPending = null });
        await sut.PollNowAsync();
        meterProvider.ForceFlush();

        Assert.AreEqual(0, ReadLatestLong(metrics.Single(m => m.Name == "nimbus.outbox.dispatch_lag")));
    }

    [TestMethod]
    public async Task Deferred_gauges_report_per_endpoint_values()
    {
        var metrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(metrics)
            .Build()!;

        var deferredQuery = new FakeDeferredMessageMetricsQuery();
        deferredQuery.Endpoints["billing"] = (Pending: 7, Blocked: 3);
        deferredQuery.Endpoints["orders"] = (Pending: 2, Blocked: null);

        using var sut = new NimBusGaugeBackgroundService(
            new TestOptionsMonitor(new NimBusOpenTelemetryOptions()),
            deferredQuery: deferredQuery);
        await sut.PollNowAsync();
        meterProvider.ForceFlush();

        var pending = metrics.Single(m => m.Name == "nimbus.deferred.pending");
        Assert.AreEqual(7, ReadLongForTag(pending, MessagingAttributes.NimBusEndpoint, "billing"));
        Assert.AreEqual(2, ReadLongForTag(pending, MessagingAttributes.NimBusEndpoint, "orders"));

        var blocked = metrics.Single(m => m.Name == "nimbus.deferred.blocked_sessions");
        Assert.AreEqual(3, ReadLongForTag(blocked, MessagingAttributes.NimBusEndpoint, "billing"));
        // orders had Blocked=null → no observation for that endpoint
        Assert.IsNull(TryReadLongForTag(blocked, MessagingAttributes.NimBusEndpoint, "orders"));
    }

    [TestMethod]
    public async Task Skips_when_no_provider_registered_and_logs_once()
    {
        var metrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(metrics)
            .Build()!;

        var capturingLogger = new CapturingLogger<NimBusGaugeBackgroundService>();
        using var sut = new NimBusGaugeBackgroundService(
            new TestOptionsMonitor(new NimBusOpenTelemetryOptions()),
            outboxQuery: null,
            deferredQuery: null,
            logger: capturingLogger);

        await sut.PollNowAsync();
        await sut.PollNowAsync(); // second poll should not double-log
        meterProvider.ForceFlush();

        Assert.AreEqual(0, CountPoints(metrics, "nimbus.outbox.pending"),
            "Outbox gauges should not be observed without IOutboxMetricsQuery");
        Assert.AreEqual(0, CountPoints(metrics, "nimbus.deferred.pending"),
            "Deferred gauges should not be observed without IDeferredMessageMetricsQuery");

        var outboxSkipLogs = capturingLogger.Records.Count(r => r.Message.Contains("nimbus.outbox.pending", StringComparison.Ordinal));
        var deferredSkipLogs = capturingLogger.Records.Count(r => r.Message.Contains("nimbus.deferred.pending", StringComparison.Ordinal));
        Assert.AreEqual(1, outboxSkipLogs, "Outbox skip should log exactly once across multiple polls");
        Assert.AreEqual(1, deferredSkipLogs, "Deferred skip should log exactly once across multiple polls");
    }

    [TestMethod]
    public async Task Cached_value_used_between_polls()
    {
        var metrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(metrics)
            .Build()!;

        var fake = new FakeOutboxMetricsQuery { PendingCount = 100 };
        using var sut = new NimBusGaugeBackgroundService(
            new TestOptionsMonitor(new NimBusOpenTelemetryOptions { GaugePollInterval = TimeSpan.FromHours(1) }),
            outboxQuery: fake);
        await sut.PollNowAsync();

        // Provider value changes but no poll fires; the gauge should still report 100.
        fake.PendingCount = 999;
        meterProvider.ForceFlush();

        var gauge = metrics.Single(m => m.Name == "nimbus.outbox.pending");
        Assert.AreEqual(100, ReadLatestLong(gauge),
            "Gauge callback must read from the cache, not call the provider synchronously");
    }

    private static int CountPoints(IEnumerable<Metric> metrics, string name)
    {
        var metric = metrics.FirstOrDefault(m => m.Name == name);
        if (metric is null) return 0;
        int count = 0;
        foreach (ref readonly var _ in metric.GetMetricPoints()) count++;
        return count;
    }

    private static long ReadLatestLong(Metric metric)
    {
        long latest = 0;
        bool any = false;
        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            latest = point.GetGaugeLastValueLong();
            any = true;
        }
        Assert.IsTrue(any, $"Metric {metric.Name} has no points");
        return latest;
    }

    private static long ReadLongForTag(Metric metric, string tagKey, string tagValue)
        => TryReadLongForTag(metric, tagKey, tagValue) ?? throw new AssertFailedException(
            $"Metric {metric.Name} has no point with {tagKey}={tagValue}");

    private static long? TryReadLongForTag(Metric metric, string tagKey, string tagValue)
    {
        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            foreach (var tag in point.Tags)
            {
                if (tag.Key == tagKey && string.Equals(tag.Value?.ToString(), tagValue, StringComparison.Ordinal))
                    return point.GetGaugeLastValueLong();
            }
        }
        return null;
    }
}

internal sealed class FakeOutboxMetricsQuery : IOutboxMetricsQuery
{
    public long PendingCount { get; set; }
    public DateTimeOffset? OldestPending { get; set; }

    public Task<long> GetPendingCountAsync(CancellationToken cancellationToken = default) => Task.FromResult(PendingCount);
    public Task<DateTimeOffset?> GetOldestPendingEnqueuedAtUtcAsync(CancellationToken cancellationToken = default) => Task.FromResult(OldestPending);
}

internal sealed class FakeDeferredMessageMetricsQuery : IDeferredMessageMetricsQuery
{
    public Dictionary<string, (long Pending, long? Blocked)> Endpoints { get; } = new();

    public Task<long> GetDeferredPendingCountAsync(string endpointId, CancellationToken cancellationToken = default)
        => Task.FromResult(Endpoints.TryGetValue(endpointId, out var v) ? v.Pending : 0L);

    public Task<long?> GetBlockedSessionCountAsync(string endpointId, CancellationToken cancellationToken = default)
        => Task.FromResult(Endpoints.TryGetValue(endpointId, out var v) ? v.Blocked : null);

    public IReadOnlyCollection<string> GetEndpointIds() => Endpoints.Keys;
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Records { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Records.Add((logLevel, formatter(state, exception)));
    }
}
