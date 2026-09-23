using System;
using System.Collections.Generic;
using System.Linq;

namespace NimBus.WebApp.Services.Simulation;

/// <summary>Why simulation is not allowed in the current environment.</summary>
public enum SimulationBlockedReason
{
    /// <summary>The <c>Environment</c> setting is missing or blank; the gate fails closed.</summary>
    EnvironmentMissing,

    /// <summary>The environment is not in <c>NimBus:Simulation:AllowedEnvironments</c>.</summary>
    NotAllowed,

    /// <summary>The environment is a production name, which no setting can allow.</summary>
    Production,
}

/// <summary>
/// The environment gate (plan Decision 4). Deliberately not
/// <c>IWebHostEnvironment.IsProduction()</c>: App Service runs the deployed dev site with the
/// default <c>ASPNETCORE_ENVIRONMENT=Production</c>, so the per-environment <c>Environment</c>
/// setting that the Bicep deploy writes is the signal.
/// </summary>
public static class SimulationEnvironmentPolicy
{
    /// <summary>Hard-coded production names. Matched case-insensitively; no setting overrides them.</summary>
    public static readonly IReadOnlySet<string> ProductionNames =
        new HashSet<string>(new[] { "prod", "production", "prd", "live" }, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="environment"/> is a production name.</summary>
    public static bool IsProductionName(string? environment) =>
        !string.IsNullOrWhiteSpace(environment) && ProductionNames.Contains(environment.Trim());

    /// <summary>
    /// Evaluates the gate. Missing or blank is blocked, a production name is blocked even when
    /// listed, and anything else must be in <paramref name="allowedEnvironments"/>.
    /// </summary>
    public static (bool Allowed, SimulationBlockedReason? Reason) Evaluate(string? environment, IEnumerable<string>? allowedEnvironments)
    {
        if (string.IsNullOrWhiteSpace(environment))
            return (false, SimulationBlockedReason.EnvironmentMissing);

        var trimmed = environment.Trim();
        if (IsProductionName(trimmed))
            return (false, SimulationBlockedReason.Production);

        var allowed = (allowedEnvironments ?? Enumerable.Empty<string>())
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Any(e => string.Equals(e.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));

        return allowed ? (true, null) : (false, SimulationBlockedReason.NotAllowed);
    }
}
