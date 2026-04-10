using System.IO.Compression;

namespace NimBus.CommandLine;

internal sealed class AppDeploymentService
{
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

        await _az.EnsureLoggedInAsync(cancellationToken).ConfigureAwait(false);

        var publishRoot = Path.Combine(Path.GetTempPath(), "nb", $"{names.SolutionId}-{names.Environment}", DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
        var resolverPublish = Path.Combine(publishRoot, "resolver");
        var webAppPublish = Path.Combine(publishRoot, "webapp");
        Directory.CreateDirectory(resolverPublish);
        Directory.CreateDirectory(webAppPublish);

        await PublishAsync(_context.ResolverProjectPath, resolverPublish, options.Configuration, cancellationToken).ConfigureAwait(false);
        await PublishAsync(_context.WebAppProjectPath, webAppPublish, options.Configuration, cancellationToken).ConfigureAwait(false);

        var resolverZip = PackagePublishOutput(resolverPublish, "resolver.zip");
        var webAppZip = PackagePublishOutput(webAppPublish, "webapp.zip");

        CliOutput.WriteLine($"Stopping '{names.ResolverFunctionAppName}' for deployment...");
        await _az.EnsureSuccessAsync(
            new[] { "functionapp", "stop", "--resource-group", options.ResourceGroupName, "--name", names.ResolverFunctionAppName },
            cancellationToken,
            $"Failed to stop '{names.ResolverFunctionAppName}'.").ConfigureAwait(false);

        try
        {
            await _az.EnsureSuccessAsync(
                new[]
                {
                    "functionapp", "deployment", "source", "config-zip",
                    "--resource-group", options.ResourceGroupName,
                    "--name", names.ResolverFunctionAppName,
                    "--src", resolverZip,
                },
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

    private async Task PublishAsync(string projectPath, string outputPath, string configuration, CancellationToken cancellationToken)
    {
        CliOutput.WriteLine($"Publishing '{projectPath}'...");
        var result = await _processRunner.RunAsync(
            "dotnet",
            new[]
            {
                "publish",
                projectPath,
                "--configuration", configuration,
                "--output", outputPath,
                "--nologo",
            },
            _context.RepositoryRoot,
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new CommandException($"dotnet publish failed for '{projectPath}'.{Environment.NewLine}{result.StandardError}");
        }
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
