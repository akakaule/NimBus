using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;
using NimBus.Core;

namespace NimBus.WebApp.Services.Simulation;

/// <summary>
/// Deploy-time configuration for the traffic simulator, bound from <c>NimBus:Simulation</c>.
/// Runtime settings (enabled flag, auto-stop, rate ceiling, ownership) start from these
/// values on every restart and can then be changed from Admin → Simulation.
/// </summary>
public sealed class SimulationOptions
{
    /// <summary>Configuration section the options bind from.</summary>
    public const string SectionName = "NimBus:Simulation";

    /// <summary>Whether simulate mode starts switched on. It still needs an allowed environment.</summary>
    public bool EnabledByDefault { get; set; }

    /// <summary>
    /// Values of the <c>Environment</c> setting in which simulation may run. Production names are
    /// rejected here and blocked at runtime regardless (<see cref="SimulationEnvironmentPolicy"/>).
    /// </summary>
    public List<string> AllowedEnvironments { get; set; } = new() { "dev", "development" };

    /// <summary>
    /// Consuming endpoints the simulator hosts handlers for from startup. Every other consuming
    /// endpoint is External: the simulator publishes to it but never competes for its subscription.
    /// </summary>
    public List<string> OwnedEndpoints { get; set; } = new();

    /// <summary>Minutes after Start before a run stops itself. 1–240.</summary>
    public int AutoStopMinutes { get; set; } = 60;

    /// <summary>Upper bound an Owner may set the rate ceiling to. 1–6000.</summary>
    public int MaxRateCeilingPerMinute { get; set; } = 1200;

    /// <summary>Initial global publish ceiling across every publisher loop. 1–<see cref="MaxRateCeilingPerMinute"/>.</summary>
    public int RateCeilingPerMinute { get; set; } = 600;

    /// <summary>Prefix on the session and correlation id of every simulated message.</summary>
    public string SessionPrefix { get; set; } = "sim-";

    /// <summary>Session keys each simulated publisher rotates through. 1–1000.</summary>
    public int SessionPoolSize { get; set; } = 40;

    /// <summary>Seconds Stop lets receivers finish in-flight work before stopping them. 0–120.</summary>
    public int DrainSeconds { get; set; } = 30;

    /// <summary>Seconds Pause (and the publisher half of Stop) waits before abandoning loops. 1–120.</summary>
    public int PauseDeadlineSeconds { get; set; } = 10;

    /// <summary>Seconds Stop waits for receivers to stop after the drain. 1–120.</summary>
    public int StopDeadlineSeconds { get; set; } = 15;
}

/// <summary>
/// Startup validation for <see cref="SimulationOptions"/>: ranges, the production-name
/// exclusion and ownership of endpoints that actually consume events.
/// </summary>
public sealed class SimulationOptionsValidator : IValidateOptions<SimulationOptions>
{
    private readonly IPlatform _platform;

    /// <summary>Creates the validator.</summary>
    public SimulationOptionsValidator(IPlatform platform)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, SimulationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        void Range(string key, int value, int min, int max)
        {
            if (value < min || value > max)
                failures.Add($"{SimulationOptions.SectionName}:{key} must be between {min} and {max} (was {value}).");
        }

        Range(nameof(options.AutoStopMinutes), options.AutoStopMinutes, SimulationLimits.MinAutoStopMinutes, SimulationLimits.MaxAutoStopMinutes);
        Range(nameof(options.MaxRateCeilingPerMinute), options.MaxRateCeilingPerMinute, 1, SimulationLimits.MaxRateCeilingPerMinute);
        Range(nameof(options.RateCeilingPerMinute), options.RateCeilingPerMinute, 1, Math.Max(1, options.MaxRateCeilingPerMinute));
        Range(nameof(options.SessionPoolSize), options.SessionPoolSize, 1, SimulationLimits.MaxSessionPoolSize);
        Range(nameof(options.DrainSeconds), options.DrainSeconds, 0, SimulationLimits.MaxDrainSeconds);
        Range(nameof(options.PauseDeadlineSeconds), options.PauseDeadlineSeconds, 1, SimulationLimits.MaxDeadlineSeconds);
        Range(nameof(options.StopDeadlineSeconds), options.StopDeadlineSeconds, 1, SimulationLimits.MaxDeadlineSeconds);

        if (string.IsNullOrWhiteSpace(options.SessionPrefix) || options.SessionPrefix.Length > SimulationLimits.MaxSessionPrefixLength)
            failures.Add($"{SimulationOptions.SectionName}:{nameof(options.SessionPrefix)} must be 1–{SimulationLimits.MaxSessionPrefixLength} characters.");

        foreach (var environment in options.AllowedEnvironments ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(environment))
            {
                failures.Add($"{SimulationOptions.SectionName}:{nameof(options.AllowedEnvironments)} must not contain blank entries.");
            }
            else if (SimulationEnvironmentPolicy.IsProductionName(environment))
            {
                failures.Add(
                    $"{SimulationOptions.SectionName}:{nameof(options.AllowedEnvironments)} contains the production name '{environment}'. " +
                    "Simulation can never run in production; remove it.");
            }
        }

        var consumers = SimulationTopology.ConsumingEndpointIds(_platform);
        foreach (var endpoint in options.OwnedEndpoints ?? new List<string>())
        {
            if (!consumers.Contains(endpoint ?? string.Empty))
            {
                failures.Add(
                    $"{SimulationOptions.SectionName}:{nameof(options.OwnedEndpoints)} names '{endpoint}', which is not an endpoint that consumes events on this platform.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>Fixed bounds shared by options, settings and config validation (plan Decision 11).</summary>
public static class SimulationLimits
{
    /// <summary>Speed multipliers the simulator accepts.</summary>
    public static readonly IReadOnlyList<double> AllowedSpeeds = new[] { 0.5, 1, 2, 5, 10, 20 };

    /// <summary>Retries of the simulator's fixed retry policy.</summary>
    public const int SimulatorMaxRetries = 3;

    /// <summary>Fixed backoff of the simulator's retry policy.</summary>
    public static readonly TimeSpan SimulatorRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>Minimum auto-stop.</summary>
    public const int MinAutoStopMinutes = 1;

    /// <summary>Maximum auto-stop.</summary>
    public const int MaxAutoStopMinutes = 240;

    /// <summary>Largest deploy-time rate ceiling.</summary>
    public const int MaxRateCeilingPerMinute = 6000;

    /// <summary>Largest session pool.</summary>
    public const int MaxSessionPoolSize = 1000;

    /// <summary>Longest drain.</summary>
    public const int MaxDrainSeconds = 120;

    /// <summary>Longest pause/stop deadline.</summary>
    public const int MaxDeadlineSeconds = 120;

    /// <summary>Longest session prefix.</summary>
    public const int MaxSessionPrefixLength = 16;

    /// <summary>Largest handler latency.</summary>
    public const int MaxLatencyMs = 10_000;

    /// <summary>Longest failure exception message.</summary>
    public const int MaxExceptionMessageLength = 512;

    /// <summary>Longest session glob.</summary>
    public const int MaxSessionPatternLength = 64;

    /// <summary>Longest revert-after.</summary>
    public const int MaxRevertAfterMinutes = 240;

    /// <summary>Deliveries the live feed keeps.</summary>
    public const int FeedSize = 100;
}

/// <summary>Reads the simulator's view of the platform topology from <see cref="IPlatform"/>.</summary>
public static class SimulationTopology
{
    /// <summary>Endpoints that consume at least one event type.</summary>
    public static HashSet<string> ConsumingEndpointIds(IPlatform platform) =>
        platform.Endpoints
            .Where(e => e.EventTypesConsumed.Any())
            .Select(e => e.Id)
            .ToHashSet(StringComparer.Ordinal);
}
