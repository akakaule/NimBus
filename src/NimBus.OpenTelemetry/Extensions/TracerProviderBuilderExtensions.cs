using OpenTelemetry.Trace;

namespace NimBus.OpenTelemetry;

/// <summary>
/// OpenTelemetry tracer-provider extension that registers every NimBus
/// activity source with one call.
/// </summary>
public static class NimBusOpenTelemetryTracerProviderBuilderExtensions
{
    /// <summary>
    /// Registers every NimBus-emitted <see cref="System.Diagnostics.ActivitySource"/>
    /// (publisher, consumer, outbox, deferred processor, resolver, store) so spans
    /// emitted by <c>NimBus.OpenTelemetry</c> are observed and exported. Idempotent.
    /// </summary>
    public static TracerProviderBuilder AddNimBusInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        foreach (var sourceName in NimBusInstrumentation.AllActivitySourceNames)
            builder.AddSource(sourceName);

        return builder;
    }
}
