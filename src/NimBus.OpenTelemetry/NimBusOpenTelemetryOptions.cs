namespace NimBus.OpenTelemetry;

/// <summary>
/// Options that tune NimBus instrumentation runtime behaviour. Bind from the
/// <c>NimBus:Otel</c> configuration section, e.g. via environment variable
/// <c>NimBus__Otel__Verbose=true</c>.
/// </summary>
public sealed class NimBusOpenTelemetryOptions
{
    /// <summary>
    /// Configuration section name bound by <c>AddNimBusInstrumentation</c>.
    /// </summary>
    public const string SectionName = "NimBus:Otel";

    /// <summary>
    /// Enable per-step child spans (<c>NimBus.Pipeline.{Behavior}</c>,
    /// <c>NimBus.Store.{Operation}</c>, etc.). Off by default — these spans are
    /// high-volume and intended for incident triage, not steady-state operation.
    /// </summary>
    public bool Verbose { get; set; }

    /// <summary>
    /// Capture an allow-list of NimBus headers as span events (never the message
    /// body). Off by default.
    /// </summary>
    public bool IncludeMessageHeaders { get; set; }

    /// <summary>
    /// Poll interval for observable gauges (outbox pending, deferred pending,
    /// blocked sessions). Default 30 seconds.
    /// </summary>
    public TimeSpan GaugePollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When set, an <see cref="System.Diagnostics.ActivityEvent"/> is emitted on
    /// the dispatcher span whenever the outbox <c>dispatch_lag</c> gauge exceeds
    /// this threshold. <c>null</c> disables the warning.
    /// </summary>
    public TimeSpan? OutboxLagWarnThreshold { get; set; }
}
