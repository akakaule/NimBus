using System;
using System.Collections.Generic;

namespace NimBus.WebApp.Services.Simulation;

/// <summary>How a simulated handler behaves (plan Decision 8).</summary>
public enum SimulatedFailureMode
{
    /// <summary>Short delay, then complete.</summary>
    Healthy,

    /// <summary>Throw a transient exception with probability <see cref="SimulatedFailure.Rate"/> percent.</summary>
    Random,

    /// <summary>Throw while the delivery's retry count is below <see cref="SimulatedFailure.FailAttempts"/>, then complete.</summary>
    Transient,

    /// <summary>Delay between the latency bounds, then complete.</summary>
    Slow,

    /// <summary>Throw a permanent exception, which the simulator's classifier dead-letters.</summary>
    Poison,

    /// <summary>Behave as if no handler is registered, producing an Unsupported response.</summary>
    NoHandler,
}

/// <summary>A subscriber's failure mode and its parameters.</summary>
public sealed record SimulatedFailure
{
    /// <summary>The mode.</summary>
    public SimulatedFailureMode Mode { get; init; } = SimulatedFailureMode.Healthy;

    /// <summary>Random: failure probability in percent, 1–100.</summary>
    public int Rate { get; init; } = 30;

    /// <summary>Transient: attempts that fail before one succeeds, 1–<see cref="SimulationLimits.SimulatorMaxRetries"/>.</summary>
    public int FailAttempts { get; init; } = 2;

    /// <summary>Slow: lower latency bound in milliseconds.</summary>
    public int LatencyMinMs { get; init; } = 800;

    /// <summary>Slow: upper latency bound in milliseconds.</summary>
    public int LatencyMaxMs { get; init; } = 2_500;

    /// <summary>Exception message; stable text so Insights grouping can be tested. Null uses the mode default.</summary>
    public string? ExceptionMessage { get; init; }

    /// <summary>Event types the mode applies to. Empty means all of the endpoint's consumed types.</summary>
    public IReadOnlyList<string> EventTypeIds { get; init; } = Array.Empty<string>();

    /// <summary>Optional <c>*</c>/<c>?</c> glob over the session id.</summary>
    public string? SessionPattern { get; init; }

    /// <summary>Minutes after the mode was applied before it reverts to Healthy. Null never reverts.</summary>
    public int? RevertAfterMinutes { get; init; }

    /// <summary>The default exception message for <paramref name="mode"/>.</summary>
    public static string DefaultMessage(SimulatedFailureMode mode) => mode switch
    {
        SimulatedFailureMode.Random => "Simulated random failure",
        SimulatedFailureMode.Transient => "Simulated transient failure",
        SimulatedFailureMode.Poison => "Simulated poison message",
        SimulatedFailureMode.NoHandler => "Simulated missing handler",
        _ => "Simulated failure",
    };

    /// <summary>The message to throw with.</summary>
    public string EffectiveMessage => string.IsNullOrWhiteSpace(ExceptionMessage) ? DefaultMessage(Mode) : ExceptionMessage!;
}

/// <summary>One event type a simulated publisher can publish.</summary>
public sealed record SimulationEventTypeConfig(string EventTypeId, bool Enabled, int RatePerMinute);

/// <summary>A simulated publisher: an endpoint and the event types it produces.</summary>
public sealed record SimulationPublisherConfig(string EndpointId, IReadOnlyList<SimulationEventTypeConfig> EventTypes);

/// <summary>A simulated subscriber: an owned endpoint and its failure mode.</summary>
public sealed record SimulationSubscriberConfig(string EndpointId, SimulatedFailure Failure);

/// <summary>The whole simulation config. Replaced as a unit.</summary>
public sealed record SimulationConfig(
    double Speed,
    IReadOnlyList<SimulationPublisherConfig> Publishers,
    IReadOnlyList<SimulationSubscriberConfig> Subscribers);

/// <summary>Runtime settings changed from Admin → Simulation.</summary>
public sealed record SimulationSettings(
    bool Enabled,
    int AutoStopMinutes,
    int RateCeilingPerMinute,
    IReadOnlyList<string> OwnedEndpoints);

/// <summary>Run state of the simulation.</summary>
public enum SimulationState
{
    /// <summary>Nothing runs.</summary>
    Stopped,

    /// <summary>Publishers and owned receivers run.</summary>
    Running,

    /// <summary>A Pause is in progress.</summary>
    Pausing,

    /// <summary>Publishers and receivers are stopped; counters are kept.</summary>
    Paused,

    /// <summary>A Stop is in progress.</summary>
    Stopping,
}

/// <summary>What a simulated handler did with one delivery.</summary>
public enum SimulatedDeliveryOutcome
{
    /// <summary>The handler completed.</summary>
    Completed,

    /// <summary>The handler threw a transient failure.</summary>
    Threw,

    /// <summary>The handler threw a permanent failure.</summary>
    Poisoned,

    /// <summary>The handler reported no handler for the event type.</summary>
    Unsupported,
}

/// <summary>One delivery seen by a simulated handler. Handler-side truth, not Resolver status.</summary>
public sealed record SimulatedDelivery(
    DateTimeOffset At,
    string EndpointId,
    string EventTypeId,
    string SessionId,
    string MessageId,
    SimulatedDeliveryOutcome Outcome,
    int Attempt,
    long LatencyMs,
    string? Error);

/// <summary>Receives deliveries recorded by simulated handlers.</summary>
public interface ISimulationFeedSink
{
    /// <summary>Records one delivery.</summary>
    void Record(SimulatedDelivery delivery);
}

/// <summary>Counters for the current run.</summary>
public sealed record SimulationCounters(
    long Published,
    long HandledOk,
    long HandlerErrors,
    long Poisoned,
    long PublishErrors,
    long AbandonedSends,
    int ThroughputPerMinute);

/// <summary>Snapshot returned by <see cref="ISimulationService.GetStatusAsync"/>.</summary>
public sealed record SimulationStatusSnapshot(
    bool Allowed,
    SimulationBlockedReason? BlockedReason,
    string? Environment,
    bool Enabled,
    SimulationState State,
    DateTimeOffset? StartedAt,
    DateTimeOffset? AutoStopAt,
    bool Capped,
    int MaxRateCeilingPerMinute,
    string SessionPrefix,
    IReadOnlyList<string> AllowedEnvironments,
    SimulationSettings Settings,
    SimulationConfig Config,
    SimulationCounters Counters,
    IReadOnlyList<SimulatedDelivery> Recent,
    IReadOnlyList<SimulationEndpointInfo> Endpoints);

/// <summary>A platform endpoint as the simulator sees it.</summary>
public sealed record SimulationEndpointInfo(
    string EndpointId,
    IReadOnlyList<string> Produces,
    IReadOnlyList<string> Consumes,
    bool Owned,
    bool LiveInstanceWarning,
    SimulatedFailureMode? EffectiveMode);
