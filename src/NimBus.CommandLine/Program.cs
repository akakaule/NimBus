using McMaster.Extensions.CommandLineUtils;
using NimBus.MessageStore;
using Spectre.Console;

namespace NimBus.CommandLine;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var app = new CommandLineApplication
        {
            Name = "nb",
            Description = "Provision NimBus infrastructure, Service Bus topology, and app deployments.",
        };

        var sbConnectionString = new CommandOption("-sbc|--sb-connection-string", CommandOptionType.SingleValue)
        {
            Description = $"Overrides environment variable '{CommandRunner.SbConnectionStringEnvName}'"
        };

        var dbConnectionString = new CommandOption("-dbc|--db-connection-string", CommandOptionType.SingleValue)
        {
            Description = $"Overrides environment variable '{CommandRunner.DbConnectionStringEnvName}'"
        };

        app.HelpOption(inherited: true);
        app.HelpTextGenerator = new ColoredHelpTextGenerator();

        ConfigureInfraCommands(app);
        ConfigureTopologyCommands(app);
        ConfigureDeployCommands(app);
        ConfigureSetupCommand(app);
        ConfigureEndpointCommands(app, sbConnectionString, dbConnectionString);
        ConfigureContainerCommands(app, sbConnectionString, dbConnectionString);

        app.OnExecute(() =>
        {
            AnsiConsole.MarkupLine("[yellow]Specify a subcommand[/]");
            app.ShowHelp();
            return 1;
        });

        try
        {
            return await app.ExecuteAsync(args).ConfigureAwait(false);
        }
        catch (CommandException exception)
        {
            CliOutput.WriteError(exception.Message);
            return 1;
        }
        catch (Exception exception)
        {
            CliOutput.WriteError($"Command failed with exception ({exception.GetType().Name}): {exception.Message}");
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

    private static void ConfigureEndpointCommands(CommandLineApplication app, CommandOption sbConnectionString, CommandOption dbConnectionString)
    {
        app.Command("endpoint", endpointCommand =>
        {
            endpointCommand.Description = "Manage Service Bus endpoints, sessions, and topics.";

            endpointCommand.OnExecute(() =>
            {
                AnsiConsole.MarkupLine("[yellow]Specify a subcommand[/]");
                endpointCommand.ShowHelp();
                return 1;
            });

            endpointCommand.Command("session", sessionCommand =>
            {
                sessionCommand.OnExecute(() =>
                {
                    AnsiConsole.MarkupLine("[yellow]Specify a subcommand[/]");
                    sessionCommand.ShowHelp();
                    return 1;
                });

                sessionCommand.Command("delete", deleteSessionCommand =>
                {
                    deleteSessionCommand.Description = "Deletes messages on a session and its session state";

                    var endpointName = deleteSessionCommand.Argument("endpoint-name", "Name of endpoint (required)").IsRequired();
                    var sessionId = deleteSessionCommand.Argument("session", "Session id (required)").IsRequired();

                    deleteSessionCommand.AddOption(sbConnectionString);
                    deleteSessionCommand.AddOption(dbConnectionString);

                    deleteSessionCommand.OnExecuteAsync(async ct =>
                    {
                        await CommandRunner.Run(sbConnectionString, dbConnectionString, (sbClient, dbClient, sbAdmin) => Endpoint.DeleteSession(sbClient, dbClient, endpointName, sessionId));
                        Console.WriteLine($"Endpoint '{endpointName.Value}' is ready.");
                    });
                });
            });

            endpointCommand.Command("topics", topicsCommand =>
            {
                topicsCommand.OnExecute(() =>
                {
                    AnsiConsole.MarkupLine("[yellow]Specify a subcommand[/]");
                    topicsCommand.ShowHelp();
                    return 1;
                });

                topicsCommand.Command("removeDeprecated", removeDeprecatedCommand =>
                {
                    removeDeprecatedCommand.Description = "Deletes deprecated topics and the underlying subscriptions and rules from the service bus";

                    var endpointName = removeDeprecatedCommand.Argument("endpoint-name", "Name of endpoint (required)").IsRequired();

                    removeDeprecatedCommand.AddOption(sbConnectionString);

                    removeDeprecatedCommand.OnExecuteAsync(async ct =>
                    {
                        await CommandRunner.Run(sbConnectionString, dbConnectionString, (sbClient, dbClient, sbAdmin) => Endpoint.RemoveDeprecated(sbAdmin, endpointName));
                    });
                });
            });

            endpointCommand.Command("purge", purgeCommand =>
            {
                purgeCommand.Description = "Purges messages from a Service Bus subscription by state and/or enqueued time";

                var endpointName = purgeCommand.Argument("endpoint-name", "Name of endpoint (required)").IsRequired();
                var subscriptionOption = purgeCommand.Option("--subscription <NAME>", "Subscription name (defaults to endpoint name)", CommandOptionType.SingleValue);
                var stateOption = purgeCommand.Option("--state <STATE>", "Comma-separated message states to purge: Active, Deferred (default: all)", CommandOptionType.SingleValue);
                var beforeOption = purgeCommand.Option("--before <UTC_DATETIME>", "Only purge messages enqueued before this UTC datetime (e.g. 2026-03-01T00:00:00)", CommandOptionType.SingleValue);
                purgeCommand.AddOption(sbConnectionString);

                purgeCommand.OnExecuteAsync(async ct =>
                {
                    var validStates = new[] { "active", "deferred" };
                    var stateFilters = new List<string>();

                    if (stateOption.HasValue())
                    {
                        foreach (var raw in stateOption.Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (!validStates.Contains(raw, StringComparer.OrdinalIgnoreCase))
                            {
                                Console.Error.WriteLine($"Invalid state '{raw}'. Valid values: Active, Deferred");
                                return 1;
                            }
                            stateFilters.Add(raw.ToLower());
                        }
                    }

                    DateTime? before = null;
                    if (beforeOption.HasValue())
                    {
                        if (!DateTime.TryParse(beforeOption.Value(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsedDate))
                        {
                            Console.Error.WriteLine($"Invalid date '{beforeOption.Value()}'. Expected format: yyyy-MM-ddTHH:mm:ss (UTC)");
                            return 1;
                        }
                        before = parsedDate;
                    }

                    string subscription = subscriptionOption.HasValue() ? subscriptionOption.Value() : endpointName.Value;

                    await CommandRunner.Run(sbConnectionString, (sbClient) =>
                        Endpoint.PurgeSubscription(sbClient, endpointName.Value, subscription, stateFilters, before));
                    return 0;
                });
            });
        });
    }

    private static void ConfigureContainerCommands(CommandLineApplication app, CommandOption sbConnectionString, CommandOption dbConnectionString)
    {
        app.Command("container", containerCommand =>
        {
            containerCommand.Description = "Manage Cosmos DB containers, events, and messages.";

            containerCommand.OnExecute(() =>
            {
                AnsiConsole.MarkupLine("[yellow]Specify a subcommand[/]");
                containerCommand.ShowHelp();
                return 1;
            });

            containerCommand.Command("event", eventCommand =>
            {
                eventCommand.OnExecute(() =>
                {
                    AnsiConsole.MarkupLine("[yellow]Specify a subcommand[/]");
                    eventCommand.ShowHelp();
                    return 1;
                });

                eventCommand.Command("delete", deleteEventCommand =>
                {
                    deleteEventCommand.Description = "Deletes a message from Cosmos DB";

                    deleteEventCommand.AddOption(dbConnectionString);

                    var endpointName = deleteEventCommand.Argument("endpoint-name", "Name of endpoint (required)").IsRequired();
                    var eventId = deleteEventCommand.Argument("event-id", "Id of event (required)").IsRequired();

                    deleteEventCommand.OnExecuteAsync(async ct =>
                    {
                        await CommandRunner.Run(dbConnectionString, (dbClient) => Container.DeleteDocument(dbClient, endpointName, eventId));
                    });
                });
            });

            containerCommand.Command("message", messageGroupCommand =>
            {
                messageGroupCommand.OnExecute(() =>
                {
                    AnsiConsole.MarkupLine("[yellow]Specify a subcommand[/]");
                    messageGroupCommand.ShowHelp();
                    return 1;
                });

                messageGroupCommand.Command("delete", deleteMessagesCommand =>
                {
                    deleteMessagesCommand.Description = "Deletes messages from the messages container filtered by the To field";

                    var toArg = deleteMessagesCommand.Argument("to", "Value of the To field to filter on (e.g. CrmEndpoint)").IsRequired();
                    deleteMessagesCommand.AddOption(dbConnectionString);

                    deleteMessagesCommand.OnExecuteAsync(async ct =>
                    {
                        var connStr = dbConnectionString.HasValue() ? dbConnectionString.Value() : Environment.GetEnvironmentVariable(CommandRunner.DbConnectionStringEnvName);
                        if (string.IsNullOrEmpty(connStr))
                        {
                            Console.Error.WriteLine($"Cosmos DB connection string is required. Use -dbc or set environment variable '{CommandRunner.DbConnectionStringEnvName}'.");
                            return 1;
                        }

                        using var cosmosClient = new Microsoft.Azure.Cosmos.CosmosClient(connStr);
                        await Container.DeleteMessages(cosmosClient, toArg.Value!);
                        return 0;
                    });
                });
            });

            containerCommand.Command("delete", deleteCommand =>
            {
                deleteCommand.Description = "Deletes messages in Cosmos DB by status";

                var endpointName = deleteCommand.Argument("endpoint-name", "Name of endpoint (required)").IsRequired();
                var statusOption = deleteCommand.Option("-s|--status <STATUS>", "Comma-separated list of statuses to delete (e.g. failed,deadlettered). Default: deadlettered", CommandOptionType.SingleValue);
                deleteCommand.AddOption(dbConnectionString);

                deleteCommand.OnExecuteAsync(async ct =>
                {
                    var statuses = new List<string>();
                    if (statusOption.HasValue())
                    {
                        foreach (var raw in statusOption.Value()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (!Enum.TryParse<ResolutionStatus>(raw, ignoreCase: true, out var status))
                            {
                                Console.Error.WriteLine($"Invalid status '{raw}'. Valid values: {string.Join(", ", Enum.GetNames<ResolutionStatus>())}");
                                return 1;
                            }
                            statuses.Add(status.ToString());
                        }
                    }
                    else
                    {
                        statuses.Add(ResolutionStatus.DeadLettered.ToString());
                    }

                    await CommandRunner.Run(dbConnectionString, (dbClient) => Container.DeleteDocuments(dbClient, endpointName, statuses));
                    return 0;
                });
            });

            containerCommand.Command("resubmit", resubmitCommand =>
            {
                resubmitCommand.Description = "Updates messages in Cosmos DB and resubmit messages";

                var endpointName = resubmitCommand.Argument("endpoint-name", "Name of endpoint (required)").IsRequired();
                resubmitCommand.AddOption(sbConnectionString);
                resubmitCommand.AddOption(dbConnectionString);

                resubmitCommand.OnExecuteAsync(async ct =>
                {
                    await CommandRunner.Run(sbConnectionString, dbConnectionString, (sbClient, dbClient) => Container.ResubmitMessages(sbClient, dbClient, endpointName));
                });
            });

            containerCommand.Command("copy", copyCommand =>
            {
                copyCommand.Description = "Copies endpoint data (events + messages) from one Cosmos DB to another";

                var endpointName = copyCommand.Argument("endpoint-name", "Name of endpoint (required)").IsRequired();
                copyCommand.AddOption(dbConnectionString);

                var targetDbConnectionString = new CommandOption("--target-dbc|--target-db-connection-string", CommandOptionType.SingleValue)
                {
                    Description = "Target Cosmos DB connection string (required)"
                };
                targetDbConnectionString.IsRequired();
                copyCommand.AddOption(targetDbConnectionString);

                var fromOption = copyCommand.Option("--from <UTC_DATETIME>", "Only copy events from this UTC datetime (e.g. 2026-03-01T00:00:00)", CommandOptionType.SingleValue);
                var toOption = copyCommand.Option("--to <UTC_DATETIME>", "Only copy events up to this UTC datetime (e.g. 2026-03-20T00:00:00)", CommandOptionType.SingleValue);
                var statusOption = copyCommand.Option("-s|--status <STATUS>", "Comma-separated list of statuses to copy (e.g. failed,deferred). Default: all", CommandOptionType.SingleValue);
                var batchSizeOption = copyCommand.Option("-b|--batch-size <SIZE>", "Number of documents to copy per batch (default: all)", CommandOptionType.SingleValue);

                copyCommand.OnExecuteAsync(async ct =>
                {
                    var statuses = new List<string>();
                    if (statusOption.HasValue())
                    {
                        foreach (var raw in statusOption.Value()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (!Enum.TryParse<ResolutionStatus>(raw, ignoreCase: true, out var status))
                            {
                                Console.Error.WriteLine($"Invalid status '{raw}'. Valid values: {string.Join(", ", Enum.GetNames<ResolutionStatus>())}");
                                return 1;
                            }
                            statuses.Add(status.ToString());
                        }
                    }

                    DateTime? from = null;
                    if (fromOption.HasValue())
                    {
                        if (!DateTime.TryParse(fromOption.Value(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsedFrom))
                        {
                            Console.Error.WriteLine($"Invalid date '{fromOption.Value()}'. Expected format: yyyy-MM-ddTHH:mm:ss (UTC)");
                            return 1;
                        }
                        from = parsedFrom;
                    }

                    DateTime? to = null;
                    if (toOption.HasValue())
                    {
                        if (!DateTime.TryParse(toOption.Value(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsedTo))
                        {
                            Console.Error.WriteLine($"Invalid date '{toOption.Value()}'. Expected format: yyyy-MM-ddTHH:mm:ss (UTC)");
                            return 1;
                        }
                        to = parsedTo;
                    }

                    int? batchSize = null;
                    if (batchSizeOption.HasValue())
                    {
                        if (!int.TryParse(batchSizeOption.Value(), out var parsedBatch) || parsedBatch <= 0)
                        {
                            Console.Error.WriteLine($"Invalid batch size '{batchSizeOption.Value()}'. Must be a positive integer.");
                            return 1;
                        }
                        batchSize = parsedBatch;
                    }

                    await CommandRunner.Run(dbConnectionString, targetDbConnectionString, (sourceClient, targetClient) =>
                        Container.CopyEndpointData(sourceClient, targetClient, endpointName, from, to, statuses, batchSize));
                    return 0;
                });
            });

            containerCommand.Command("skip", skipCommand =>
            {
                skipCommand.Description = "Marks messages as Skipped in Cosmos DB";

                var endpointName = skipCommand.Argument("endpoint-name", "Name of endpoint (required)").IsRequired();
                var statusOption = skipCommand.Option("-s|--status <STATUS>", "Comma-separated list of source statuses to skip (e.g. failed,deadlettered)", CommandOptionType.SingleValue);
                statusOption.IsRequired();
                var beforeOption = skipCommand.Option("--before <UTC_DATETIME>", "Only skip messages last updated before this UTC datetime (e.g. 2026-03-01T00:00:00)", CommandOptionType.SingleValue);
                skipCommand.AddOption(dbConnectionString);

                skipCommand.OnExecuteAsync(async ct =>
                {
                    var terminalStatuses = new[] { ResolutionStatus.Completed, ResolutionStatus.Skipped };
                    var parsed = new List<string>();

                    foreach (var raw in statusOption.Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!Enum.TryParse<ResolutionStatus>(raw, ignoreCase: true, out var status))
                        {
                            Console.Error.WriteLine($"Invalid status '{raw}'. Valid values: {string.Join(", ", Enum.GetNames<ResolutionStatus>())}");
                            return 1;
                        }
                        if (terminalStatuses.Contains(status))
                        {
                            Console.Error.WriteLine($"Cannot skip events that are already '{status}'.");
                            return 1;
                        }
                        parsed.Add(status.ToString());
                    }

                    DateTime? before = null;
                    if (beforeOption.HasValue())
                    {
                        if (!DateTime.TryParse(beforeOption.Value(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsedDate))
                        {
                            Console.Error.WriteLine($"Invalid date '{beforeOption.Value()}'. Expected format: yyyy-MM-ddTHH:mm:ss (UTC)");
                            return 1;
                        }
                        before = parsedDate;
                    }

                    await CommandRunner.Run(dbConnectionString, (dbClient) => Container.SkipMessages(dbClient, endpointName, parsed, before));
                    return 0;
                });
            });
        });
    }
}
