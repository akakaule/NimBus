#pragma warning disable CA1707, CA1861, CA2007

using System.Text.Json;
using Xunit;

namespace NimBus.CommandLine.Tests;

public sealed class InfrastructureDeployerSecretTests
{
    [Fact]
    public async Task ApplyAsync_KeepsEveryDeploymentSecretOutOfAzureCliArguments()
    {
        const string sqlPassword = "sql-password-marker";
        const string identityPassword = "identity-password-marker";
        var azureCli = new RecordingAzureCliRunner();
        var deployer = new InfrastructureDeployer(new CommandContext(Path.GetTempPath()), azureCli);
        var options = new InfrastructureOptions(
            "nimbus",
            "dev",
            "rg-nimbus-dev",
            ResourceNamePostFix: null,
            Location: null,
            WebAppVersion: "test",
            StorageProviderChoice.SqlServer,
            SqlProvisioningMode.Provision,
            SqlConnectionString: null,
            SqlAdminLogin: "nimbusadmin",
            SqlAdminPassword: sqlPassword,
            SqlServerName: null,
            ResolverPlan: null,
            IdentityAdminEmail: "admin@example.com",
            IdentityAdminPassword: identityPassword);

        await deployer.ApplyAsync(options, CancellationToken.None);

        Assert.Equal(2, azureCli.Deployments.Count);
        Assert.All(
            azureCli.Deployments,
            deployment =>
            {
                Assert.DoesNotContain(deployment.Arguments, argument => argument.Contains(sqlPassword, StringComparison.Ordinal));
                Assert.DoesNotContain(deployment.Arguments, argument => argument.Contains(identityPassword, StringComparison.Ordinal));
                Assert.DoesNotContain(deployment.Arguments, argument => argument.Contains(RecordingAzureCliRunner.InstrumentationKey, StringComparison.Ordinal));
            });

        Assert.Equal(sqlPassword, azureCli.Deployments[0].SecureParameters["sqlAdminPassword"]);
        Assert.Equal(RecordingAzureCliRunner.InstrumentationKey, azureCli.Deployments[1].SecureParameters["instrumentationKey"]);
        Assert.Contains(sqlPassword, azureCli.Deployments[1].SecureParameters["sqlConnectionString"], StringComparison.Ordinal);
        Assert.Equal(identityPassword, azureCli.Deployments[1].SecureParameters["identityAdminPassword"]);
    }

    [Fact]
    public async Task ApplyAsync_LeavesApplicationInsightsApiKeysAlone()
    {
        // The Application Insights query API stopped accepting API keys on 2026-03-31. The
        // WebApp queries with its managed identity, so the CLI neither creates nor deletes keys
        // and no longer passes the deprecated apiKey template parameter.
        var azureCli = new RecordingAzureCliRunner();
        var deployer = new InfrastructureDeployer(new CommandContext(Path.GetTempPath()), azureCli);
        var options = new InfrastructureOptions(
            "nimbus",
            "dev",
            "rg-nimbus-dev",
            ResourceNamePostFix: null,
            Location: null,
            WebAppVersion: "test");

        await deployer.ApplyAsync(options, CancellationToken.None);

        Assert.DoesNotContain(azureCli.Commands, command => command.Contains("api-key", StringComparer.Ordinal));
        var webAppDeployment = azureCli.Deployments[1];
        Assert.False(webAppDeployment.SecureParameters.ContainsKey("apiKey"));
        Assert.DoesNotContain(webAppDeployment.Arguments, argument => argument.StartsWith("apiKey=", StringComparison.Ordinal));
    }
}
