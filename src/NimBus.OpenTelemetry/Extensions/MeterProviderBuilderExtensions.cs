using NimBus.Core.Diagnostics;
using OpenTelemetry.Metrics;

namespace NimBus.OpenTelemetry;

/// <summary>
/// OpenTelemetry meter-provider extension that registers every NimBus meter
/// with one call.
/// </summary>
public static class NimBusOpenTelemetryMeterProviderBuilderExtensions
{
    /// <summary>
    /// Registers every NimBus-emitted meter (publisher, consumer, outbox,
    /// deferred processor, resolver, store) so that instruments emitted by
    /// <c>NimBus.OpenTelemetry</c> are observed and exported. Idempotent.
    /// </summary>
    public static MeterProviderBuilder AddNimBusInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        foreach (var meterName in NimBusInstrumentation.AllMeterNames)
            builder.AddMeter(meterName);

        return builder;
    }
}
