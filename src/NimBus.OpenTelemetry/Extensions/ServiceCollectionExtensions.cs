using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NimBus.OpenTelemetry.Instrumentation;

namespace NimBus.OpenTelemetry;

/// <summary>
/// DI registration entry point for NimBus instrumentation. This extension owns
/// options binding, decorators, and the gauge background service — the
/// <c>MeterProviderBuilder</c> and <c>TracerProviderBuilder</c> extensions only
/// register meter / source names with the OTel SDK and cannot deliver runtime
/// components.
/// </summary>
public static class NimBusOpenTelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Registers NimBus instrumentation runtime components: options bound from
    /// the <c>NimBus:Otel</c> configuration section (when an
    /// <see cref="IConfiguration"/> is available) and the publisher / store
    /// decorators applied by the SDK and storage provider extensions.
    ///
    /// Idempotent — repeated calls are no-ops.
    /// </summary>
    public static IServiceCollection AddNimBusInstrumentation(
        this IServiceCollection services,
        Action<NimBusOpenTelemetryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Idempotency: drop out if we've already registered.
        if (services.Any(d => d.ServiceType == typeof(NimBusInstrumentationMarker)))
        {
            if (configure is not null)
                services.Configure(configure);
            return services;
        }

        services.TryAddSingleton<NimBusInstrumentationMarker>();

        services.AddOptions<NimBusOpenTelemetryOptions>()
            .BindConfiguration(NimBusOpenTelemetryOptions.SectionName);

        if (configure is not null)
            services.Configure(configure);

        // Gauge background service polls IOutboxMetricsQuery /
        // IDeferredMessageMetricsQuery (when registered) and caches the results
        // so the OTel observable-gauge callbacks are non-blocking.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, NimBusGaugeBackgroundService>());

        return services;
    }
}

/// <summary>
/// Sentinel registered by <c>AddNimBusInstrumentation</c> to keep that call
/// idempotent.
/// </summary>
internal sealed class NimBusInstrumentationMarker
{
}
