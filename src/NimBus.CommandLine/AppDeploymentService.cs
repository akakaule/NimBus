using System.IO.Compression;
using System.Text.Json;

namespace NimBus.CommandLine;

internal sealed class AppDeploymentService
{
    private static readonly Version MinimumFlexConsumptionAzureCliVersion = new(2, 60, 0);
    private static readonly string[] AzureCliVersionArguments = ["version", "--output", "json"];

    private readonly AzureCliRunner _az;
    private readonly IDeploymentArtifactSource _artifacts;

    public AppDeploymentService(AzureCliRunner az, IDeploymentArtifactSource artifacts)
    {
        _az = az;
        _artifacts = artifacts;
    }

    public async Task DeployAsync(AppDeploymentOptions options, CancellationToken cancellationToken)
    {
        var names = NamingConventions.Build(options.SolutionId, options.Environment);
        var deployResolver = options.Target != AppDeploymentTarget.WebApp;
        var deployWebApp = options.Target != AppDeploymentTarget.Resolver;

        await _az.EnsureLoggedInAsync(cancellationToken).ConfigureAwait(false);

        var version = await _artifacts.GetVersionAsync(cancellationToken).ConfigureAwait(false);
        CliOutput.WriteLine(version != null
            ? $"Deploying NimBus {version}."
            : "No version could be determined; deploying with the default version.");

        if (deployResolver)
        {
            var isFlexConsumption = await IsFlexConsumptionResolverAsync(options.ResourceGroupName, names.CoreAppServicePlanName, cancellationToken).ConfigureAwait(false);
            if (isFlexConsumption)
            {
                await EnsureAzureCliSupportsFlexConsumptionAsync(cancellationToken).ConfigureAwait(false);
            }

            var resolverZip = await _artifacts.GetResolverZipAsync(cancellationToken).ConfigureAwait(false);
            await DeployResolverAsync(options, names, resolverZip, isFlexConsumption, cancellationToken).ConfigureAwait(false);
        }

        if (deployWebApp)
        {
            var webAppZip = await _artifacts.GetWebAppZipAsync(cancellationToken).ConfigureAwait(false);

            await _az.EnsureSuccessAsync(
                new[]
                {
                    "webapp", "deploy",
                    "--resource-group", options.ResourceGroupName,
                    "--name", names.WebAppName,
                    "--src-path", webAppZip,
                    "--type", "zip",
                },
                cancellationToken,
                $"Failed to deploy the web app '{names.WebAppName}'.").ConfigureAwait(false);
        }
    }

    private async Task DeployResolverAsync(AppDeploymentOptions options, DeploymentNames names, string resolverZip, bool isFlexConsumption, CancellationToken cancellationToken)
    {
        var deployZipArguments = new[]
        {
            "functionapp", "deployment", "source", "config-zip",
            "--resource-group", options.ResourceGroupName,
            "--name", names.ResolverFunctionAppName,
            "--src", resolverZip,
        };

        if (isFlexConsumption)
        {
            // Flex Consumption deployments must run against a STARTED app: after
            // publishing, the az CLI polls the host status endpoint and reports the
            // deployment as failed when the app is stopped. OneDeploy swaps the
            // package atomically, so the stop/start dance is unnecessary anyway.
            CliOutput.WriteLine($"Deploying '{names.ResolverFunctionAppName}' (Flex Consumption)...");
            await _az.EnsureSuccessAsync(
                deployZipArguments,
                cancellationToken,
                $"Failed to deploy the resolver app '{names.ResolverFunctionAppName}'.").ConfigureAwait(false);
        }
        else
        {
            CliOutput.WriteLine($"Stopping '{names.ResolverFunctionAppName}' for deployment...");
            await _az.EnsureSuccessAsync(
                new[] { "functionapp", "stop", "--resource-group", options.ResourceGroupName, "--name", names.ResolverFunctionAppName },
                cancellationToken,
                $"Failed to stop '{names.ResolverFunctionAppName}'.").ConfigureAwait(false);

            try
            {
                await _az.EnsureSuccessAsync(
                    deployZipArguments,
                    cancellationToken,
                    $"Failed to deploy the resolver app '{names.ResolverFunctionAppName}'.").ConfigureAwait(false);
            }
            finally
            {
                CliOutput.WriteLine($"Starting '{names.ResolverFunctionAppName}'...");
                await _az.EnsureSuccessAsync(
                    new[] { "functionapp", "start", "--resource-group", options.ResourceGroupName, "--name", names.ResolverFunctionAppName },
                    CancellationToken.None,
                    $"Failed to start '{names.ResolverFunctionAppName}'.").ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> IsFlexConsumptionResolverAsync(string resourceGroupName, string coreAppServicePlanName, CancellationToken cancellationToken)
    {
        var tier = await _az.CaptureValueAsync(
            new[]
            {
                "appservice", "plan", "show",
                "--resource-group", resourceGroupName,
                "--name", coreAppServicePlanName,
                "--query", "sku.tier",
                "--output", "tsv",
            },
            cancellationToken,
            $"Failed to read the core App Service Plan '{coreAppServicePlanName}'. Run 'nb infra apply' first.").ConfigureAwait(false);

        return string.Equals(tier.Trim(), "FlexConsumption", StringComparison.OrdinalIgnoreCase);
    }

    private async Task EnsureAzureCliSupportsFlexConsumptionAsync(CancellationToken cancellationToken)
    {
        using var versionDocument = await _az.CaptureJsonAsync(
            AzureCliVersionArguments,
            cancellationToken,
            "Failed to read the Azure CLI version.").ConfigureAwait(false);

        var rawVersion = versionDocument.RootElement.TryGetProperty("azure-cli", out var azureCliVersion)
            ? azureCliVersion.GetString()
            : null;

        // Pre-2.60 CLIs push Flex Consumption zips to the legacy Kudu zipdeploy
        // endpoint, which the Flex SCM front-end rejects at the TLS layer — the
        // resulting SSLEOFError blames a proxy and hides the real cause.
        if (Version.TryParse(rawVersion, out var installedVersion) && installedVersion < MinimumFlexConsumptionAzureCliVersion)
        {
            throw new CommandException(
                $"Azure CLI {rawVersion} cannot deploy to Flex Consumption function apps; version {MinimumFlexConsumptionAzureCliVersion} or later is required. Upgrade with 'az upgrade' (or 'winget upgrade Microsoft.AzureCLI') and retry.");
        }
    }
}
