using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NimBus.Core.Diagnostics;
using NimBus.Core.Outbox;

namespace NimBus.OpenTelemetry.Instrumentation;

/// <summary>
/// Hosted service that polls registered metrics-query providers on a fixed
/// cadence (<see cref="NimBusOpenTelemetryOptions.GaugePollInterval"/>) and
/// caches the latest value so the OTel observable-gauge callbacks return
/// synchronously without blocking the export thread (FR-044).
///
/// Skip-when-missing semantics (FR-052): if no provider for a given gauge is
/// registered, the corresponding instruments are not observed, and a single
/// INFO log line records the skip on first poll. Hosts that *want* the gauge
/// register the provider (<see cref="IOutboxMetricsQuery"/>,
/// <see cref="IDeferredMessageMetricsQuery"/>); hosts that don't pay nothing.
/// </summary>
internal sealed class NimBusGaugeBackgroundService : BackgroundService
{
    private readonly IOutboxMetricsQuery? _outboxQuery;
    private readonly IDeferredMessageMetricsQuery? _deferredQuery;
    private readonly IOptionsMonitor<NimBusOpenTelemetryOptions> _options;
    private readonly ILogger<NimBusGaugeBackgroundService> _logger;
    private readonly ConcurrentDictionary<string, long> _outboxCache = new();
    private readonly ConcurrentDictionary<(string Endpoint, string Metric), long> _deferredCache = new();
    private bool _outboxSkipLogged;
    private bool _deferredSkipLogged;
    // ObservableGauge callbacks live for the lifetime of the static Meter; we
    // can't unregister them. Once the service is disposed (or stops), the
    // callbacks must observe nothing so they don't interfere with later
    // process-wide consumers (e.g. test isolation, reload scenarios).
    private volatile bool _stopped;

    public NimBusGaugeBackgroundService(
        IOptionsMonitor<NimBusOpenTelemetryOptions> options,
        IOutboxMetricsQuery? outboxQuery = null,
        IDeferredMessageMetricsQuery? deferredQuery = null,
        ILogger<NimBusGaugeBackgroundService>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _outboxQuery = outboxQuery;
        _deferredQuery = deferredQuery;
        _logger = logger ?? NullLogger<NimBusGaugeBackgroundService>.Instance;

        RegisterGauges();
    }

    private void RegisterGauges()
    {
        // Outbox depth + lag.
        NimBusMeters.Outbox.CreateObservableGauge(
            "nimbus.outbox.pending",
            ObserveOutboxPending,
            unit: "{messages}",
            description: "Outbox rows waiting to be dispatched.");

        NimBusMeters.Outbox.CreateObservableGauge(
            "nimbus.outbox.dispatch_lag",
            ObserveOutboxDispatchLag,
            unit: "s",
            description: "Seconds since the oldest pending outbox row was enqueued.");

        // Deferred depth + blocked sessions (per endpoint).
        NimBusMeters.DeferredProcessor.CreateObservableGauge(
            "nimbus.deferred.pending",
            ObserveDeferredPending,
            unit: "{messages}",
            description: "Messages currently parked on the deferred subscription.");

        NimBusMeters.DeferredProcessor.CreateObservableGauge(
            "nimbus.deferred.blocked_sessions",
            ObserveBlockedSessions,
            unit: "{sessions}",
            description: "Distinct sessions currently blocked.");
    }

    private IEnumerable<Measurement<long>> ObserveOutboxPending() =>
        !_stopped && _outboxCache.TryGetValue("pending", out var v)
            ? new[] { new Measurement<long>(v) }
            : Array.Empty<Measurement<long>>();

    private IEnumerable<Measurement<long>> ObserveOutboxDispatchLag() =>
        !_stopped && _outboxCache.TryGetValue("dispatch_lag_s", out var v)
            ? new[] { new Measurement<long>(v) }
            : Array.Empty<Measurement<long>>();

    private IEnumerable<Measurement<long>> ObserveDeferredPending() =>
        EmitDeferred("pending");

    private IEnumerable<Measurement<long>> ObserveBlockedSessions() =>
        EmitDeferred("blocked_sessions");

    private IEnumerable<Measurement<long>> EmitDeferred(string metric)
    {
        if (_stopped) return Array.Empty<Measurement<long>>();
        var output = new List<Measurement<long>>();
        foreach (var ((endpoint, m), value) in _deferredCache.Select(kv => (kv.Key, kv.Value)))
        {
            if (!string.Equals(m, metric, StringComparison.Ordinal))
                continue;
            output.Add(new Measurement<long>(value, new KeyValuePair<string, object?>(MessagingAttributes.NimBusEndpoint, endpoint)));
        }
        return output;
    }

    public override void Dispose()
    {
        _stopped = true;
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Prime the cache before the first OTel collection cycle so callbacks
        // don't return empty for an entire export interval.
        await PollAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(_options.CurrentValue.GaugePollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await PollAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown — expected.
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        await PollOutboxAsync(cancellationToken).ConfigureAwait(false);
        await PollDeferredAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PollOutboxAsync(CancellationToken cancellationToken)
    {
        if (_outboxQuery is null)
        {
            if (!_outboxSkipLogged)
            {
                _logger.LogInformation(
                    "nimbus.outbox.pending and nimbus.outbox.dispatch_lag will not be reported because no IOutboxMetricsQuery is registered.");
                _outboxSkipLogged = true;
            }
            return;
        }

        try
        {
            var pending = await _outboxQuery.GetPendingCountAsync(cancellationToken).ConfigureAwait(false);
            _outboxCache["pending"] = pending;

            var oldest = await _outboxQuery.GetOldestPendingEnqueuedAtUtcAsync(cancellationToken).ConfigureAwait(false);
            if (oldest.HasValue)
            {
                var lag = (long)Math.Max(0, (DateTimeOffset.UtcNow - oldest.Value).TotalSeconds);
                _outboxCache["dispatch_lag_s"] = lag;
            }
            else
            {
                _outboxCache["dispatch_lag_s"] = 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Outbox gauge poll failed; previous cached values retained.");
        }
    }

    private async Task PollDeferredAsync(CancellationToken cancellationToken)
    {
        if (_deferredQuery is null)
        {
            if (!_deferredSkipLogged)
            {
                _logger.LogInformation(
                    "nimbus.deferred.pending and nimbus.deferred.blocked_sessions will not be reported because no IDeferredMessageMetricsQuery is registered.");
                _deferredSkipLogged = true;
            }
            return;
        }

        var endpoints = _deferredQuery.GetEndpointIds();
        foreach (var endpoint in endpoints)
        {
            try
            {
                var pending = await _deferredQuery.GetDeferredPendingCountAsync(endpoint, cancellationToken).ConfigureAwait(false);
                _deferredCache[(endpoint, "pending")] = pending;

                var blocked = await _deferredQuery.GetBlockedSessionCountAsync(endpoint, cancellationToken).ConfigureAwait(false);
                if (blocked.HasValue)
                    _deferredCache[(endpoint, "blocked_sessions")] = blocked.Value;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Deferred gauge poll failed for endpoint {Endpoint}; previous cached values retained.", endpoint);
            }
        }
    }

    /// <summary>
    /// Test-only synchronous poll. Production code drives polling via
    /// <see cref="ExecuteAsync(CancellationToken)"/>.
    /// </summary>
    internal Task PollNowAsync(CancellationToken cancellationToken = default) => PollAsync(cancellationToken);
}
