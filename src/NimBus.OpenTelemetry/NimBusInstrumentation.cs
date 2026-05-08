namespace NimBus.OpenTelemetry;

/// <summary>
/// Canonical names for every <see cref="System.Diagnostics.ActivitySource"/> and
/// <see cref="System.Diagnostics.Metrics.Meter"/> emitted by NimBus. Use these constants
/// when wiring an OpenTelemetry pipeline by hand instead of <c>AddNimBusInstrumentation()</c>.
/// </summary>
public static class NimBusInstrumentation
{
    // ActivitySources (FR-010).

    /// <summary>Source of <c>NimBus.Publish</c> spans (Producer).</summary>
    public const string PublisherActivitySourceName = "NimBus.Publisher";

    /// <summary>Source of <c>NimBus.Process</c> spans (Consumer).</summary>
    public const string ConsumerActivitySourceName = "NimBus.Consumer";

    /// <summary>Source of <c>NimBus.Outbox.*</c> spans.</summary>
    public const string OutboxActivitySourceName = "NimBus.Outbox";

    /// <summary>Source of <c>NimBus.DeferredProcessor.*</c> spans.</summary>
    public const string DeferredProcessorActivitySourceName = "NimBus.DeferredProcessor";

    /// <summary>Source of <c>NimBus.Resolver.*</c> spans.</summary>
    public const string ResolverActivitySourceName = "NimBus.Resolver";

    /// <summary>Source of <c>NimBus.Store.*</c> spans (verbose-only by default).</summary>
    public const string StoreActivitySourceName = "NimBus.Store";

    // Meters (FR-040).

    /// <summary>Meter that emits publisher counters and histograms.</summary>
    public const string PublisherMeterName = "NimBus.Publisher";

    /// <summary>Meter that emits consumer counters and histograms.</summary>
    public const string ConsumerMeterName = "NimBus.Consumer";

    /// <summary>Meter that emits outbox counters, histograms, and gauges.</summary>
    public const string OutboxMeterName = "NimBus.Outbox";

    /// <summary>Meter that emits deferred-processor counters, histograms, and gauges.</summary>
    public const string DeferredProcessorMeterName = "NimBus.DeferredProcessor";

    /// <summary>Meter that emits resolver counters and histograms.</summary>
    public const string ResolverMeterName = "NimBus.Resolver";

    /// <summary>Meter that emits message-store counters and histograms.</summary>
    public const string StoreMeterName = "NimBus.Store";

    /// <summary>The set of every NimBus activity source name. Suitable for bulk <c>AddSource(...)</c> calls.</summary>
    public static readonly IReadOnlyList<string> AllActivitySourceNames =
    [
        PublisherActivitySourceName,
        ConsumerActivitySourceName,
        OutboxActivitySourceName,
        DeferredProcessorActivitySourceName,
        ResolverActivitySourceName,
        StoreActivitySourceName,
    ];

    /// <summary>The set of every NimBus meter name. Suitable for bulk <c>AddMeter(...)</c> calls.</summary>
    public static readonly IReadOnlyList<string> AllMeterNames =
    [
        PublisherMeterName,
        ConsumerMeterName,
        OutboxMeterName,
        DeferredProcessorMeterName,
        ResolverMeterName,
        StoreMeterName,
    ];

    /// <summary>
    /// Wraps an inner <see cref="NimBus.Core.Messages.ISender"/> with the publisher
    /// instrumentation decorator. Transport providers call this when registering
    /// the publisher pipeline so the publish span and publish counters are
    /// emitted automatically. The <paramref name="messagingSystem"/> argument
    /// determines the value of the <c>messaging.system</c> attribute on the
    /// resulting span (e.g. <see cref="Semantics.MessagingSystem.ServiceBus"/>).
    /// </summary>
    public static NimBus.Core.Messages.ISender InstrumentSender(
        NimBus.Core.Messages.ISender inner,
        string messagingSystem)
        => new Instrumentation.InstrumentingSenderDecorator(inner, messagingSystem);
}
