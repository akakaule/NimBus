using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace NimBus.Extensions.IntegrationIntelligence;

/// <summary>Telemetry names for advisory failure classification.</summary>
public static class NimBusIntelligenceTelemetry
{
    /// <summary>Meter and source name.</summary>
    public const string Name = "NimBus.Intelligence";

    /// <summary>Activity source.</summary>
    public static readonly ActivitySource ActivitySource = new(Name);

    /// <summary>Meter.</summary>
    public static readonly Meter Meter = new(Name);

    /// <summary>Request counter.</summary>
    public static readonly Counter<long> Requests = Meter.CreateCounter<long>("nimbus.intelligence.failure_classification.requests");

    /// <summary>Duration histogram in milliseconds.</summary>
    public static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("nimbus.intelligence.failure_classification.duration", "ms");

    /// <summary>Error counter.</summary>
    public static readonly Counter<long> Errors = Meter.CreateCounter<long>("nimbus.intelligence.failure_classification.errors");

    /// <summary>Provider input-token counter.</summary>
    public static readonly Counter<long> InputTokens = Meter.CreateCounter<long>("nimbus.intelligence.failure_classification.input_tokens");
}
