#pragma warning disable CA1707, CA2007

using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// The Elastic Premium Resolver keeps its code on the Azure Files share named by
/// WEBSITE_CONTENTSHARE. The name used to be derived from a GUID the CLI generated on every
/// run, so each redeployment repointed the Resolver at a new, empty share. An existing
/// Resolver now keeps its share, and a new one gets a stable name.
/// </summary>
public sealed class InfrastructureDeployerContentShareTests
{
    private const string ExistingShare = "func-nimbus-dev-resolver8f3k2m9x1q7wz";

    [Fact]
    public async Task ApplyAsync_KeepsTheExistingResolversContentShare()
    {
        var azureCli = ExistingResolver(shareSetting: ExistingShare);

        await Deployer(azureCli).ApplyAsync(Options(), CancellationToken.None);

        Assert.Contains($"existingResolverContentShare={ExistingShare}", azureCli.Deployments[0].Arguments);
    }

    [Fact]
    public async Task ApplyAsync_NoLongerSendsAPerRunSeed()
    {
        var azureCli = ExistingResolver(shareSetting: ExistingShare);

        await Deployer(azureCli).ApplyAsync(Options(), CancellationToken.None);

        Assert.DoesNotContain(azureCli.Deployments[0].Arguments, a => a.StartsWith("uniqueDeploy=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApplyAsync_NewResolverUsesTheTemplatesStableName()
    {
        var azureCli = new RecordingAzureCliRunner();

        await Deployer(azureCli).ApplyAsync(Options(), CancellationToken.None);

        Assert.DoesNotContain(azureCli.Deployments[0].Arguments, a => a.StartsWith("existingResolverContentShare=", StringComparison.Ordinal));
        Assert.DoesNotContain(azureCli.Commands, c => c.Contains("appsettings", StringComparer.Ordinal));
    }

    /// <summary>Guessing would move the Resolver to another share, so an unreadable setting stops the run.</summary>
    [Fact]
    public async Task ApplyAsync_StopsWhenTheExistingShareCannotBeRead()
    {
        var azureCli = ExistingResolver(shareSetting: ExistingShare);
        azureCli.FailWhen = arguments => arguments.Contains("appsettings");

        var error = await Assert.ThrowsAsync<CommandException>(() => Deployer(azureCli).ApplyAsync(Options(), CancellationToken.None));

        Assert.Contains("WEBSITE_CONTENTSHARE", error.Message, StringComparison.Ordinal);
        Assert.Empty(azureCli.Deployments);
    }

    [Fact]
    public async Task ApplyAsync_FlexConsumptionHasNoContentShareToKeep()
    {
        var azureCli = ExistingResolver(shareSetting: ExistingShare);

        await Deployer(azureCli).ApplyAsync(Options() with { ResolverPlan = ResolverPlanChoice.FlexConsumption }, CancellationToken.None);

        Assert.DoesNotContain(azureCli.Commands, c => c.Contains("appsettings", StringComparer.Ordinal));
    }

    [Fact]
    public void Template_derives_the_share_name_from_stable_inputs_only()
    {
        var core = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "deploy", "bicep", "deploy.core.bicep"));

        Assert.Contains("uniqueString(resourceGroup().id, resolverFunctionAppName)", core, StringComparison.Ordinal);
        Assert.DoesNotContain("uniqueString(uniqueDeploy)", core, StringComparison.Ordinal);
    }

    private static InfrastructureDeployer Deployer(RecordingAzureCliRunner azureCli) =>
        new(new CommandContext(Path.GetTempPath()), azureCli);

    private static InfrastructureOptions Options() =>
        new(
            "nimbus",
            "dev",
            "rg-nimbus-dev",
            ResourceNamePostFix: null,
            Location: "westeurope",
            WebAppVersion: "test",
            ResolverPlan: ResolverPlanChoice.ElasticPremium);

    private static RecordingAzureCliRunner ExistingResolver(string shareSetting) => new()
    {
        Responder = arguments =>
            arguments.Contains("resource") && arguments.Contains("list")
                ? """[{"name":"func-nimbus-dev-resolver","location":"westeurope"}]"""
                : arguments.Contains("appsettings") ? shareSetting
                : null,
    };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "deploy", "bicep", "deploy.core.bicep")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
