using McMaster.Extensions.CommandLineUtils;

namespace NimBus.CommandLine;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var app = new CommandLineApplication
        {
            Name = "nb",
            Description = "Provision NimBus infrastructure, Service Bus topology, and app deployments.",
        };

        app.HelpOption(inherited: true);

        ConfigureInfraCommands(app);
        ConfigureTopologyCommands(app);
        ConfigureDeployCommands(app);
        ConfigureSetupCommand(app);

        app.OnExecute(() =>
        {
            app.ShowHelp();
            return 1;
        });

        try
        {
            return await app.ExecuteAsync(args).ConfigureAwait(false);
        }
        catch (CommandException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Command failed with exception ({exception.GetType().Name}): {exception.Message}");
            return 1;
        }
    }

    private static void ConfigureInfraCommands(CommandLineApplication app)
    {
        app.Command("infra", infraCommand =>
        {
            infraCommand.Description = "Deploy Azure infrastructure using the repository bicep definitions.";
            infraCommand.HelpOption(inherited: true);

            infraCommand.Command("apply", applyCommand =>
            {
                applyCommand.Description = "Deploy the core and web app infrastructure resources.";
                applyCommand.HelpOption(inherited: true);

                var solutionId = applyCommand.Option("--solution-id <ID>", "Solution identifier used in Azure resource names.", CommandOptionType.SingleValue).IsRequired();
                var environment = applyCommand.Option("--environment <NAME>", "Environment name used in Azure resource names.", CommandOptionType.SingleValue).IsRequired();
                var resourceGroup = applyCommand.Option("--resource-group <NAME>", "Azure resource group name.", CommandOptionType.SingleValue).IsRequired();
                var repoRoot = applyCommand.Option("--repo-root <PATH>", "Repository root. Defaults to the current directory or a parent directory containing deploy/ and src/.", CommandOptionType.SingleValue);
                var location = applyCommand.Option("--location <AZURE-REGION>", "Optional location override passed to the bicep templates.", CommandOptionType.SingleValue);
                var resourceNamePostfix = applyCommand.Option("--resource-name-postfix <VALUE>", "Reserved for compatibility with the legacy pipeline scripts.", CommandOptionType.SingleValue);
                var webAppVersion = applyCommand.Option("--webapp-version <VALUE>", "Version string stored in the web app settings.", CommandOptionType.SingleValue);

                applyCommand.OnExecuteAsync(async cancellationToken =>
                {
                    var context = CommandContext.Create(repoRoot.Value());
                    var az = new AzureCliRunner();
                    var deployer = new InfrastructureDeployer(context, az);

                    var options = new InfrastructureOptions(
                        solutionId.Value(),
                        environment.Value(),
                        resourceGroup.Value(),
                        resourceNamePostfix.Value(),
                        location.Value(),
                        webAppVersion.HasValue() ? webAppVersion.Value()! : $"local-{DateTime.UtcNow:yyyyMMddHHmmss}");

                    await deployer.ApplyAsync(options, cancellationToken).ConfigureAwait(false);
                    return 0;
                });
            });
        });
    }

    private static void ConfigureTopologyCommands(CommandLineApplication app)
    {
        app.Command("topology", topologyCommand =>
        {
            topologyCommand.Description = "Export or apply the NimBus Service Bus topology.";
            topologyCommand.HelpOption(inherited: true);

            topologyCommand.Command("export", exportCommand =>
            {
                exportCommand.Description = "Export the current PlatformConfiguration to JSON.";
                exportCommand.HelpOption(inherited: true);

                var output = exportCommand.Option("-o|--output <PATH>", "Output path. Defaults to platform-config.json in the current directory.", CommandOptionType.SingleValue);

                exportCommand.OnExecuteAsync(async cancellationToken =>
                {
                    var exporter = new PlatformConfigExporter();
                    var outputPath = output.HasValue() ? output.Value()! : Path.Combine(Environment.CurrentDirectory, "platform-config.json");
                    await exporter.ExportAsync(outputPath, cancellationToken).ConfigureAwait(false);
                    return 0;
                });
            });

            topologyCommand.Command("apply", applyCommand =>
            {
                applyCommand.Description = "Provision the Service Bus topology for the current PlatformConfiguration.";
                applyCommand.HelpOption(inherited: true);

                var solutionId = applyCommand.Option("--solution-id <ID>", "Solution identifier used in Azure resource names.", CommandOptionType.SingleValue).IsRequired();
                var environment = applyCommand.Option("--environment <NAME>", "Environment name used in Azure resource names.", CommandOptionType.SingleValue).IsRequired();
                var resourceGroup = applyCommand.Option("--resource-group <NAME>", "Azure resource group containing the Service Bus namespace.", CommandOptionType.SingleValue).IsRequired();

                applyCommand.OnExecuteAsync(async cancellationToken =>
                {
                    var az = new AzureCliRunner();
                    var provisioner = new ServiceBusTopologyProvisioner(az);
                    var options = new TopologyOptions(solutionId.Value(), environment.Value(), resourceGroup.Value());

                    await provisioner.ApplyAsync(options, cancellationToken).ConfigureAwait(false);
                    return 0;
                });
            });
        });
    }

    private static void ConfigureDeployCommands(CommandLineApplication app)
    {
        app.Command("deploy", deployCommand =>
        {
            deployCommand.Description = "Build and deploy NimBus applications.";
            deployCommand.HelpOption(inherited: true);

            deployCommand.Command("apps", appsCommand =>
            {
                appsCommand.Description = "Build the resolver and web app locally, package them, and deploy them to Azure.";
                appsCommand.HelpOption(inherited: true);

                var solutionId = appsCommand.Option("--solution-id <ID>", "Solution identifier used in Azure resource names.", CommandOptionType.SingleValue).IsRequired();
                var environment = appsCommand.Option("--environment <NAME>", "Environment name used in Azure resource names.", CommandOptionType.SingleValue).IsRequired();
                var resourceGroup = appsCommand.Option("--resource-group <NAME>", "Azure resource group containing the target apps.", CommandOptionType.SingleValue).IsRequired();
                var repoRoot = appsCommand.Option("--repo-root <PATH>", "Repository root. Defaults to the current directory or a parent directory containing deploy/ and src/.", CommandOptionType.SingleValue);
                var configuration = appsCommand.Option("--configuration <NAME>", "Build configuration passed to dotnet publish.", CommandOptionType.SingleValue);

                appsCommand.OnExecuteAsync(async cancellationToken =>
                {
                    var context = CommandContext.Create(repoRoot.Value());
                    var az = new AzureCliRunner();
                    var deployer = new AppDeploymentService(context, az);
                    var options = new AppDeploymentOptions(
                        solutionId.Value(),
                        environment.Value(),
                        resourceGroup.Value(),
                        configuration.HasValue() ? configuration.Value()! : "Release");

                    await deployer.DeployAsync(options, cancellationToken).ConfigureAwait(false);
                    return 0;
                });
            });
        });
    }

    private static void ConfigureSetupCommand(CommandLineApplication app)
    {
        app.Command("setup", setupCommand =>
        {
            setupCommand.Description = "Run infrastructure deployment, topology provisioning, and app deployment in sequence.";
            setupCommand.HelpOption(inherited: true);

            var solutionId = setupCommand.Option("--solution-id <ID>", "Solution identifier used in Azure resource names.", CommandOptionType.SingleValue).IsRequired();
            var environment = setupCommand.Option("--environment <NAME>", "Environment name used in Azure resource names.", CommandOptionType.SingleValue).IsRequired();
            var resourceGroup = setupCommand.Option("--resource-group <NAME>", "Azure resource group name.", CommandOptionType.SingleValue).IsRequired();
            var repoRoot = setupCommand.Option("--repo-root <PATH>", "Repository root. Defaults to the current directory or a parent directory containing deploy/ and src/.", CommandOptionType.SingleValue);
            var location = setupCommand.Option("--location <AZURE-REGION>", "Optional location override passed to the bicep templates.", CommandOptionType.SingleValue);
            var resourceNamePostfix = setupCommand.Option("--resource-name-postfix <VALUE>", "Reserved for compatibility with the legacy pipeline scripts.", CommandOptionType.SingleValue);
            var webAppVersion = setupCommand.Option("--webapp-version <VALUE>", "Version string stored in the web app settings.", CommandOptionType.SingleValue);
            var configuration = setupCommand.Option("--configuration <NAME>", "Build configuration passed to dotnet publish.", CommandOptionType.SingleValue);

            setupCommand.OnExecuteAsync(async cancellationToken =>
            {
                var context = CommandContext.Create(repoRoot.Value());
                var az = new AzureCliRunner();
                var infra = new InfrastructureDeployer(context, az);
                var topology = new ServiceBusTopologyProvisioner(az);
                var apps = new AppDeploymentService(context, az);

                var infraOptions = new InfrastructureOptions(
                    solutionId.Value(),
                    environment.Value(),
                    resourceGroup.Value(),
                    resourceNamePostfix.Value(),
                    location.Value(),
                    webAppVersion.HasValue() ? webAppVersion.Value()! : $"local-{DateTime.UtcNow:yyyyMMddHHmmss}");

                var topologyOptions = new TopologyOptions(solutionId.Value(), environment.Value(), resourceGroup.Value());
                var appOptions = new AppDeploymentOptions(
                    solutionId.Value(),
                    environment.Value(),
                    resourceGroup.Value(),
                    configuration.HasValue() ? configuration.Value()! : "Release");

                await infra.ApplyAsync(infraOptions, cancellationToken).ConfigureAwait(false);
                await topology.ApplyAsync(topologyOptions, cancellationToken).ConfigureAwait(false);
                await apps.DeployAsync(appOptions, cancellationToken).ConfigureAwait(false);
                return 0;
            });
        });
    }
}
