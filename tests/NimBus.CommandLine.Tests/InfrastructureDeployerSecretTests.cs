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

    private sealed class RecordingAzureCliRunner : IAzureCliRunner
    {
        internal const string ApiKey = "app-insights-api-key-marker";
        internal const string InstrumentationKey = "instrumentation-key-marker";

        public List<RecordedDeployment> Deployments { get; } = new();

        public Task EnsureLoggedInAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnsureExtensionAsync(string extensionName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnsureSuccessAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken,
            string failureMessage) => Task.CompletedTask;

        public Task<ProcessResult> TryRunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            var output = arguments.Contains("list", StringComparer.Ordinal) || arguments.Contains("show", StringComparer.Ordinal)
                ? "[]"
                : string.Empty;
            return Task.FromResult(new ProcessResult(0, output, string.Empty));
        }

        public Task<string> CaptureValueAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken,
            string failureMessage)
        {
            if (arguments.Contains("api-key", StringComparer.Ordinal) && arguments.Contains("create", StringComparer.Ordinal))
            {
                return Task.FromResult(ApiKey);
            }

            var queryIndex = Array.IndexOf(arguments.ToArray(), "--query");
            var query = queryIndex >= 0 ? arguments[queryIndex + 1] : string.Empty;
            return Task.FromResult(query switch
            {
                "appId" => "app-insights-app-id",
                "instrumentationKey" => InstrumentationKey,
                _ => throw new InvalidOperationException($"Unexpected capture command: {string.Join(' ', arguments)}"),
            });
        }

        public Task EnsureSuccessAsync(
            IReadOnlyList<string> arguments,
            string? workingDirectory,
            CancellationToken cancellationToken,
            string failureMessage)
        {
            var parameterFileReference = Assert.Single(arguments, argument => argument.StartsWith('@'));
            using var document = JsonDocument.Parse(File.ReadAllText(parameterFileReference[1..]));
            var secureParameters = document.RootElement.GetProperty("parameters")
                .EnumerateObject()
                .ToDictionary(
                    parameter => parameter.Name,
                    parameter => parameter.Value.GetProperty("value").GetString() ?? string.Empty,
                    StringComparer.Ordinal);
            Deployments.Add(new RecordedDeployment(arguments.ToArray(), secureParameters));
            return Task.CompletedTask;
        }
    }

    private sealed record RecordedDeployment(
        IReadOnlyList<string> Arguments,
        IReadOnlyDictionary<string, string> SecureParameters);
}
