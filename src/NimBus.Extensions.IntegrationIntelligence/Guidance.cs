namespace NimBus.Extensions.IntegrationIntelligence;

/// <summary>Composes operator guidance from provider signals without recovery side effects.</summary>
public static class FailureGuidanceRules
{
    /// <summary>Applies the ordered guidance rules from the specification.</summary>
    public static FailureGuidance Compose(FailureIntelligenceProviderResult result, FailureClassificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);

        if (result.CategoryConfidence < options.MinimumCategoryConfidence)
        {
            return FailureGuidance.Uncertain;
        }

        if (string.Equals(result.Category, "transient_dependency", StringComparison.Ordinal)
            && result.RetryLikelihood >= options.RetryLikely)
        {
            return FailureGuidance.RetryMayHelp;
        }

        if (result.ChangeRequiredLikelihood >= options.ChangeRequired)
        {
            return FailureGuidance.ChangeLikelyRequired;
        }

        return FailureGuidance.Investigate;
    }
}
