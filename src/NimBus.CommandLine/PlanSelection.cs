namespace NimBus.CommandLine;

/// <summary>
/// Pure resolution logic for hosting-plan choices: explicit CLI flag wins, then the
/// plan that already exists in the resource group (Azure cannot convert a plan
/// between Elastic Premium and Flex Consumption in place), then the default.
/// </summary>
internal static class PlanSelection
{
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
