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
                Assert.DoesNotContain(deployment.Arguments, argument => argument.Contains(RecordingAzureCliRunner.ApiKey, StringComparison.Ordinal));
                Assert.DoesNotContain(deployment.Arguments, argument => argument.Contains(RecordingAzureCliRunner.InstrumentationKey, StringComparison.Ordinal));
            });

        Assert.Equal(sqlPassword, azureCli.Deployments[0].SecureParameters["sqlAdminPassword"]);
        Assert.Equal(RecordingAzureCliRunner.ApiKey, azureCli.Deployments[1].SecureParameters["apiKey"]);
        Assert.Equal(RecordingAzureCliRunner.InstrumentationKey, azureCli.Deployments[1].SecureParameters["instrumentationKey"]);
        Assert.Contains(sqlPassword, azureCli.Deployments[1].SecureParameters["sqlConnectionString"], StringComparison.Ordinal);
        Assert.Equal(identityPassword, azureCli.Deployments[1].SecureParameters["identityAdminPassword"]);
    }
}
