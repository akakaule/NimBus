using System.IO.Compression;
using System.Text.Json;

namespace NimBus.CommandLine;

internal sealed class AppDeploymentService
{
    private static readonly Version MinimumFlexConsumptionAzureCliVersion = new(2, 60, 0);
    private static readonly string[] AzureCliVersionArguments = ["version", "--output", "json"];

    private readonly CommandContext _context;
    private readonly AzureCliRunner _az;
    private readonly ProcessRunner _processRunner = new();

    public AppDeploymentService(CommandContext context, AzureCliRunner az)
    {
        _context = context;
        _az = az;
    }

    public async Task DeployAsync(AppDeploymentOptions options, CancellationToken cancellationToken)
    {
        var names = NamingConventions.Build(options.SolutionId, options.Environment);
        var deployResolver = options.Target != AppDeploymentTarget.WebApp;
        var deployWebApp = options.Target != AppDeploymentTarget.Resolver;

        await _az.EnsureLoggedInAsync(cancellationToken).ConfigureAwait(false);

        var publishRoot = Path.Combine(Path.GetTempPath(), "nb", $"{names.SolutionId}-{names.Environment}", DateTime.UtcNow.ToString("yyyyMMddHHmmss"));

        // Stamp the published assemblies with the latest git tag so the WebApp
        // footer (and /api/app/stats platformVersion) reports a real version
        // instead of the Directory.Build.props 0.0.0 placeholder.
        var version = await ResolveVersionFromGitTagAsync(cancellationToken).ConfigureAwait(false);
        CliOutput.WriteLine(version != null
            ? $"Stamping version {version} (latest git tag)."
            : "No git tag found; publishing with the default version.");

        if (deployResolver)
        {
            var isFlexConsumption = await IsFlexConsumptionResolverAsync(options.ResourceGroupName, names.CoreAppServicePlanName, cancellationToken).ConfigureAwait(false);
            if (isFlexConsumption)
            {
                await EnsureAzureCliSupportsFlexConsumptionAsync(cancellationToken).ConfigureAwait(false);
            }

            var resolverPublish = Path.Combine(publishRoot, "resolver");
            Directory.CreateDirectory(resolverPublish);
            await PublishAsync(_context.ResolverProjectPath, resolverPublish, options.Configuration, version, cancellationToken).ConfigureAwait(false);
            var resolverZip = PackagePublishOutput(resolverPublish, "resolver.zip");
            await DeployResolverAsync(options, names, resolverZip, isFlexConsumption, cancellationToken).ConfigureAwait(false);
        }

        if (deployWebApp)
        {
            var webAppPublish = Path.Combine(publishRoot, "webapp");
            Directory.CreateDirectory(webAppPublish);
            await PublishAsync(_context.WebAppProjectPath, webAppPublish, options.Configuration, version, cancellationToken).ConfigureAwait(false);
            var webAppZip = PackagePublishOutput(webAppPublish, "webapp.zip");

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

    private async Task PublishAsync(string projectPath, string outputPath, string configuration, string? version, CancellationToken cancellationToken)
    {
        CliOutput.WriteLine($"Publishing '{projectPath}'...");
        var arguments = new List<string>
        {
            "publish",
            projectPath,
            "--configuration", configuration,
            "--output", outputPath,
            "--nologo",
        };
        if (version != null)
        {
            arguments.Add($"-p:Version={version}");
        }

        var result = await _processRunner.RunAsync(
            "dotnet",
            arguments,
            _context.RepositoryRoot,
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new CommandException($"dotnet publish failed for '{projectPath}'.{Environment.NewLine}{result.StandardError}");
        }
    }

    private async Task<string?> ResolveVersionFromGitTagAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Latest reachable tag (e.g. "v1.2.0"); commits since the tag keep
            // the same base version — the tag is the release marker.
            var result = await _processRunner.RunAsync(
                "git",
                new[] { "describe", "--tags", "--abbrev=0" },
                _context.RepositoryRoot,
                echoStandardOutput: false,
                cancellationToken).ConfigureAwait(false);

            return result.Succeeded && TryNormalizeTagVersion(result.StandardOutput, out var version)
                ? version
                : null;
        }
        catch (CommandException)
        {
            // git not on PATH — version stamping is best-effort, never fatal.
            return null;
        }
    }

    /// <summary>
    /// Normalizes a release tag ("v1.2.0", "1.2.0-rc.1") to an MSBuild Version
    /// value. The numeric core must parse as a version; prerelease suffixes pass
    /// through verbatim.
    /// </summary>
    internal static bool TryNormalizeTagVersion(string? tag, out string version)
    {
        version = string.Empty;
        var value = tag?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var core = value.Split('-')[0];
        if (!Version.TryParse(core, out _))
        {
            return false;
        }

        version = value;
        return true;
    }

    private static string PackagePublishOutput(string publishDirectory, string zipFileName)
    {
        var zipPath = Path.Combine(Path.GetDirectoryName(publishDirectory)!, zipFileName);
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        ZipFile.CreateFromDirectory(publishDirectory, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        CliOutput.WriteLine($"Created deployment package '{zipPath}'.");
        return zipPath;
    }
}
