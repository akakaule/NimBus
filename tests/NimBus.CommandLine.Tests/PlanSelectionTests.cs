using NimBus.CommandLine;
using Xunit;

namespace NimBus.CommandLine.Tests;

public class PlanSelectionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseResolverPlanOption_ReturnsNullForAbsentValue(string? value)
    {
        Assert.Null(PlanSelection.ParseResolverPlanOption(value));
    }

    [Theory]
    [InlineData("ElasticPremium", false)]
    [InlineData("elastic-premium", false)]
    [InlineData("EP1", false)]
    [InlineData("premium", false)]
    [InlineData("FlexConsumption", true)]
    [InlineData("flex-consumption", true)]
    [InlineData("flex", true)]
    [InlineData("FC1", true)]
    public void ParseResolverPlanOption_ParsesKnownValues(string value, bool expectFlex)
    {
        var expected = expectFlex ? ResolverPlanChoice.FlexConsumption : ResolverPlanChoice.ElasticPremium;
        Assert.Equal(expected, PlanSelection.ParseResolverPlanOption(value));
    }

    [Fact]
    public void ParseResolverPlanOption_ThrowsOnUnknownValue()
    {
        Assert.Throws<CommandException>(() => PlanSelection.ParseResolverPlanOption("consumption"));
    }

    [Fact]
    public void ResolveResolverPlan_DefaultsToFlexConsumptionWhenNothingExists()
    {
        Assert.Equal(ResolverPlanChoice.FlexConsumption, PlanSelection.ResolveResolverPlan(null, null));
        Assert.Equal(ResolverPlanChoice.FlexConsumption, PlanSelection.ResolveResolverPlan(null, string.Empty));
    }

    [Theory]
    [InlineData("ElasticPremium", false)]
    [InlineData("elasticpremium", false)]
    [InlineData("FlexConsumption", true)]
    public void ResolveResolverPlan_PinsExistingPlanWhenNoExplicitChoice(string existingTier, bool expectFlex)
    {
        var expected = expectFlex ? ResolverPlanChoice.FlexConsumption : ResolverPlanChoice.ElasticPremium;
        Assert.Equal(expected, PlanSelection.ResolveResolverPlan(null, existingTier));
    }

    [Fact]
    public void ResolveResolverPlan_ExplicitChoiceWinsWhenNothingExists()
    {
        Assert.Equal(ResolverPlanChoice.ElasticPremium, PlanSelection.ResolveResolverPlan(ResolverPlanChoice.ElasticPremium, null));
        Assert.Equal(ResolverPlanChoice.FlexConsumption, PlanSelection.ResolveResolverPlan(ResolverPlanChoice.FlexConsumption, null));
    }

    [Fact]
    public void ResolveResolverPlan_ExplicitChoiceMatchingExistingPlanSucceeds()
    {
        Assert.Equal(
            ResolverPlanChoice.ElasticPremium,
            PlanSelection.ResolveResolverPlan(ResolverPlanChoice.ElasticPremium, "ElasticPremium"));
    }

    [Fact]
    public void ResolveResolverPlan_ThrowsOnConflictWithExistingPlan()
    {
        var flexOnEp = Assert.Throws<CommandException>(
            () => PlanSelection.ResolveResolverPlan(ResolverPlanChoice.FlexConsumption, "ElasticPremium"));
        Assert.Contains("Delete both the resolver Function App and the core App Service Plan", flexOnEp.Message, StringComparison.Ordinal);

        var epOnFlex = Assert.Throws<CommandException>(
            () => PlanSelection.ResolveResolverPlan(ResolverPlanChoice.ElasticPremium, "FlexConsumption"));
        Assert.Contains("Delete both the resolver Function App and the core App Service Plan", epOnFlex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveResolverPlan_UnknownExistingTierFallsBackToDefault()
    {
        Assert.Equal(ResolverPlanChoice.FlexConsumption, PlanSelection.ResolveResolverPlan(null, "PremiumV3"));
    }

    [Theory]
    [InlineData("dev", "B1")]
    [InlineData("Dev", "B1")]
    [InlineData("development", "B1")]
    [InlineData("prod", "S1")]
    [InlineData("test", "S1")]
    [InlineData("staging", "S1")]
    public void ResolveManagementPlanSku_UsesEnvironmentDefaultWhenNothingExists(string environment, string expected)
    {
        Assert.Equal(expected, PlanSelection.ResolveManagementPlanSku(null, null, environment));
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("prod")]
    public void ResolveManagementPlanSku_PinsExistingSkuWhenNoExplicitSku(string environment)
    {
        Assert.Equal("S1", PlanSelection.ResolveManagementPlanSku(null, "S1", environment));
    }

    [Fact]
    public void ResolveManagementPlanSku_ExplicitSkuWinsOverExistingAndDefault()
    {
        Assert.Equal("P1v3", PlanSelection.ResolveManagementPlanSku("P1v3", "S1", "dev"));
    }

    [Theory]
    [InlineData("B1", true)]
    [InlineData("S1", true)]
    [InlineData("P1v3", true)]
    [InlineData("F1", false)]
    [InlineData("f1", false)]
    [InlineData("D1", false)]
    public void SupportsAlwaysOn_RejectsFreeAndSharedTiers(string skuName, bool expected)
    {
        Assert.Equal(expected, PlanSelection.SupportsAlwaysOn(skuName));
    }
}
