using System.Globalization;

namespace NimBus.CommandLine;

/// <summary>
/// Pure resolution logic for hosting-plan choices: explicit CLI flag wins, then the
/// plan that already exists in the resource group (Azure cannot convert a plan
/// between Elastic Premium and Flex Consumption in place), then the default.
/// Also owns the Resolver capacity options (--resolver-max-sessions and
/// --resolver-max-instances), whose valid range depends on the resolved plan.
/// </summary>
internal static class PlanSelection
{
    /// <summary>Range of <c>--resolver-max-sessions</c> (Service Bus sessions per Resolver instance); mirrors the Bicep parameter bounds.</summary>
    public const int MinResolverSessions = 1;
    public const int MaxResolverSessions = 200;

    /// <summary>Elastic Premium <c>functionAppScaleLimit</c>: 0 means no per-app cap; the plan template allows 10 workers.</summary>
    public const int MaxElasticPremiumInstances = 10;

    /// <summary>Flex Consumption <c>maximumInstanceCount</c>: the platform accepts 1 to 1000 (template default 100).</summary>
    public const int MinFlexInstances = 1;
    public const int MaxFlexInstances = 1000;

    /// <summary>Parses the --resolver-max-sessions option value. Null/blank means "use the template default".</summary>
    public static int? ParseResolverMaxSessionsOption(string? value) =>
        ParseIntegerOption(value, "--resolver-max-sessions", MinResolverSessions, MaxResolverSessions);

    /// <summary>
    /// Parses the --resolver-max-instances option value. Null/blank means "use the template default".
    /// Only the shape is checked here; the plan-specific range is applied by <see cref="ResolveResolverMaxInstances"/>.
    /// </summary>
    public static int? ParseResolverMaxInstancesOption(string? value) =>
        ParseIntegerOption(value, "--resolver-max-instances", 0, int.MaxValue);

    /// <summary>
    /// Maps the requested Resolver instance ceiling onto the Bicep parameter for the resolved plan.
    /// Elastic Premium caps the app through functionAppScaleLimit (0 = no cap, written explicitly so an
    /// earlier cap is cleared); Flex Consumption uses maximumInstanceCount (1-1000), where 0 has no
    /// "no cap" meaning, so the two cannot share a parameter.
    /// </summary>
    public static (string ParameterName, int Value)? ResolveResolverMaxInstances(int? requested, ResolverPlanChoice plan)
    {
        if (requested is not { } value) return null;

        return plan switch
        {
            ResolverPlanChoice.ElasticPremium when value >= 0 && value <= MaxElasticPremiumInstances => ("resolverMaxInstances", value),
            ResolverPlanChoice.ElasticPremium => throw new CommandException(
                $"--resolver-max-instances {value} is out of range. Elastic Premium allows 0 (no cap) to {MaxElasticPremiumInstances}."),
            ResolverPlanChoice.FlexConsumption when value >= MinFlexInstances && value <= MaxFlexInstances => ("resolverFlexMaximumInstanceCount", value),
            ResolverPlanChoice.FlexConsumption => throw new CommandException(
                $"--resolver-max-instances {value} is out of range. Flex Consumption requires {MinFlexInstances} to {MaxFlexInstances}; omit the option to keep the template default."),
            _ => throw new ArgumentOutOfRangeException(nameof(plan), plan, "Unknown resolver plan."),
        };
    }

    private static int? ParseIntegerOption(string? value, string optionName, int min, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= min && parsed <= max)
        {
            return parsed;
        }

        var expected = max == int.MaxValue ? $"an integer of at least {min}" : $"an integer from {min} to {max}";
        throw new CommandException($"Invalid {optionName} value '{value}'. Expected {expected}.");
    }

    /// <summary>Parses the --resolver-plan option value. Null/blank means "auto" (pin existing, else default).</summary>
    public static ResolverPlanChoice? ParseResolverPlanOption(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "elasticpremium" or "ep1" or "premium" => ResolverPlanChoice.ElasticPremium,
            "flexconsumption" or "flex" or "fc1" => ResolverPlanChoice.FlexConsumption,
            _ => throw new CommandException($"Unknown --resolver-plan value '{value}'. Expected 'ElasticPremium' or 'FlexConsumption'."),
        };
    }

    public static ResolverPlanChoice ResolveResolverPlan(ResolverPlanChoice? explicitChoice, string? existingSkuTier)
    {
        var existingPlan = ParseSkuTier(existingSkuTier);

        if (explicitChoice is { } requested)
        {
            if (existingPlan is { } existing && existing != requested)
            {
                throw new CommandException(
                    $"The existing core App Service Plan is {existing} but --resolver-plan requested {requested}. " +
                    "Azure cannot convert a plan between Elastic Premium (Windows) and Flex Consumption (Linux) in place. " +
                    "Delete both the resolver Function App and the core App Service Plan first, then re-run the deployment.");
            }

            return requested;
        }

        return existingPlan ?? ResolverPlanChoice.FlexConsumption;
    }

    public static string ResolveManagementPlanSku(string? explicitSku, string? existingSkuName, string environment)
    {
        if (!string.IsNullOrWhiteSpace(explicitSku)) return explicitSku.Trim();
        if (!string.IsNullOrWhiteSpace(existingSkuName)) return existingSkuName.Trim();
        return IsDevelopmentEnvironment(environment) ? "B1" : "S1";
    }

    /// <summary>Free (F1) and Shared (D1) tiers reject the Always On site setting.</summary>
    public static bool SupportsAlwaysOn(string skuName)
    {
        var normalized = skuName.Trim().ToUpperInvariant();
        return normalized is not ("F1" or "D1" or "FREE" or "SHARED");
    }

    private static bool IsDevelopmentEnvironment(string environment) =>
        environment.Trim().ToLowerInvariant() is "dev" or "development";

    // Unknown tiers (a manually reconfigured plan) resolve to null: an explicit
    // choice still applies verbatim and the auto path falls back to the default,
    // leaving any genuine conflict to surface in the bicep deployment.
    private static ResolverPlanChoice? ParseSkuTier(string? skuTier) => skuTier?.Trim().ToLowerInvariant() switch
    {
        "elasticpremium" => ResolverPlanChoice.ElasticPremium,
        "flexconsumption" => ResolverPlanChoice.FlexConsumption,
        _ => null,
    };
}
