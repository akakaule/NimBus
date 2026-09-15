#pragma warning disable CA1707, CA2007

using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// The Resolver capacity options travel to Bicep as plain template parameters on the core
/// deployment; the resolved plan decides which instance-ceiling parameter carries the value.
/// </summary>
public sealed class InfrastructureDeployerCapacityTests
{
    [Fact]
    public async Task ApplyAsync_PassesElasticPremiumCapacityParametersToCoreDeployment()
    {
        var azureCli = new RecordingAzureCliRunner();
        var deployer = new InfrastructureDeployer(new CommandContext(Path.GetTempPath()), azureCli);
        var options = CreateOptions(ResolverPlanChoice.ElasticPremium, resolverMaxConcurrentSessions: 32, resolverMaxInstances: 3);

        await deployer.ApplyAsync(options, CancellationToken.None);

        var coreArguments = azureCli.Deployments[0].Arguments;
        Assert.Contains("resolverMaxConcurrentSessions=32", coreArguments);
        Assert.Contains("resolverMaxInstances=3", coreArguments);
        Assert.DoesNotContain(coreArguments, argument => argument.StartsWith("resolverFlexMaximumInstanceCount=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApplyAsync_PassesFlexConsumptionInstanceCeilingToCoreDeployment()
    {
        var azureCli = new RecordingAzureCliRunner();
        var deployer = new InfrastructureDeployer(new CommandContext(Path.GetTempPath()), azureCli);
        var options = CreateOptions(ResolverPlanChoice.FlexConsumption, resolverMaxConcurrentSessions: null, resolverMaxInstances: 40);

        await deployer.ApplyAsync(options, CancellationToken.None);

        var coreArguments = azureCli.Deployments[0].Arguments;
        Assert.Contains("resolverFlexMaximumInstanceCount=40", coreArguments);
        Assert.DoesNotContain(coreArguments, argument => argument.StartsWith("resolverMaxInstances=", StringComparison.Ordinal));
        Assert.DoesNotContain(coreArguments, argument => argument.StartsWith("resolverMaxConcurrentSessions=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_OmitsCapacityParametersWhenNotRequested(bool flex)
    {
        var plan = flex ? ResolverPlanChoice.FlexConsumption : ResolverPlanChoice.ElasticPremium;
        var azureCli = new RecordingAzureCliRunner();
        var deployer = new InfrastructureDeployer(new CommandContext(Path.GetTempPath()), azureCli);
        var options = CreateOptions(plan, resolverMaxConcurrentSessions: null, resolverMaxInstances: null);

        await deployer.ApplyAsync(options, CancellationToken.None);

        var coreArguments = azureCli.Deployments[0].Arguments;
        Assert.DoesNotContain(coreArguments, argument => argument.StartsWith("resolverMaxConcurrentSessions=", StringComparison.Ordinal));
        Assert.DoesNotContain(coreArguments, argument => argument.StartsWith("resolverMaxInstances=", StringComparison.Ordinal));
        Assert.DoesNotContain(coreArguments, argument => argument.StartsWith("resolverFlexMaximumInstanceCount=", StringComparison.Ordinal));
    }

    private static InfrastructureOptions CreateOptions(ResolverPlanChoice plan, int? resolverMaxConcurrentSessions, int? resolverMaxInstances) =>
        new(
            "nimbus",
            "dev",
            "rg-nimbus-dev",
            ResourceNamePostFix: null,
            Location: null,
            WebAppVersion: "test",
            ResolverPlan: plan,
            ResolverMaxConcurrentSessions: resolverMaxConcurrentSessions,
            ResolverMaxInstances: resolverMaxInstances);
}
