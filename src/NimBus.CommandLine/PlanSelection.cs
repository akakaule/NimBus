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
    /// <summary>Parses the --resolver-max-sessions option value. Null/blank means "use the template default".</summary>
    public static int? ParseResolverMaxSessionsOption(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sessions) && sessions is >= 1 and <= 200)
        {
            return sessions;
        }

        throw new CommandException($"Invalid --resolver-max-sessions value '{value}'. Expected an integer from 1 to 200.");
    }

    /// <summary>
    /// Parses the --resolver-max-instances option value. Null/blank means "use the template default".
    /// Only the shape is checked here; the plan-specific range is applied by <see cref="ResolveResolverMaxInstances"/>.
    /// </summary>
    public static int? ParseResolverMaxInstancesOption(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var instances) && instances >= 0)
        {
            return instances;
        }

        throw new CommandException($"Invalid --resolver-max-instances value '{value}'. Expected a non-negative integer.");
    }

    /// <summary>
    /// Maps the requested Resolver instance ceiling onto the Bicep parameter for the resolved plan.
    /// Elastic Premium caps the app through functionAppScaleLimit (0 = no cap, at most the plan's 10 workers);
    /// Flex Consumption uses maximumInstanceCount, whose platform minimum is 40, so the two cannot share a parameter.
    /// </summary>
    public static (string ParameterName, int Value)? ResolveResolverMaxInstances(int? requested, ResolverPlanChoice plan)
    {
        if (requested is not { } value) return null;

        return plan switch
        {
            ResolverPlanChoice.ElasticPremium when value is >= 0 and <= 10 => ("resolverMaxInstances", value),
            ResolverPlanChoice.ElasticPremium => throw new CommandException(
                $"--resolver-max-instances {value} is out of range. Elastic Premium allows 0 (no cap) to 10."),
            ResolverPlanChoice.FlexConsumption when value is >= 40 and <= 1000 => ("resolverFlexMaximumInstanceCount", value),
            ResolverPlanChoice.FlexConsumption => throw new CommandException(
                $"--resolver-max-instances {value} is out of range. Flex Consumption requires 40 to 1000; the platform minimum is 40."),
            _ => throw new ArgumentOutOfRangeException(nameof(plan), plan, "Unknown resolver plan."),
        };
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
