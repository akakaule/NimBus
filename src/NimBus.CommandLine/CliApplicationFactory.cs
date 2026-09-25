using McMaster.Extensions.CommandLineUtils;
using NimBus.MessageStore;
using Spectre.Console;

namespace NimBus.CommandLine;

/// <summary>
/// Builds the <c>nb</c> command graph. <c>Program.Main</c> owns the returned
/// application (it is disposable) and executes it; tests build it to inspect commands.
/// </summary>
internal static class CliApplicationFactory
{
    /// <summary>Creates the root <c>nb</c> application with every command registered.</summary>
    internal static CommandLineApplication Create()
    {
        var app = new CommandLineApplication
        {
            Name = "nb",
            Description = "Provision NimBus infrastructure, Service Bus topology, and app deployments.",
        };

        var sbConnectionString = new CommandOption("-sbc|--sb-connection-string", CommandOptionType.SingleValue)
        {
            Description = "Service Bus connection string, or a fully qualified namespace (e.g. mybus.servicebus.windows.net) to authenticate with Entra ID (DefaultAzureCredential). " +
                $"Overrides environment variable '{CommandRunner.SbConnectionStringEnvName}'"
        };

        var dbConnectionString = new CommandOption("-dbc|--db-connection-string", CommandOptionType.SingleValue)
        {
            Description = "Cosmos DB connection string, or an account endpoint URI (e.g. https://myaccount.documents.azure.com/) to authenticate with Entra ID (DefaultAzureCredential). " +
                $"Overrides environment variable '{CommandRunner.DbConnectionStringEnvName}'"
        };

        // Deliberately no DefaultValue: McMaster treats HasValue() as true whenever a
        // DefaultValue is set, which would make the environment-variable fallback
        // unreachable. The default lives in CosmosDbMessageStoreOptions.
        var unresolvedRetentionDays = new CommandOption("--unresolved-retention-days", CommandOptionType.SingleValue)
        {
            Description = "Retention in days stamped on unresolved (Pending/Failed/Deferred/DeadLettered/Unsupported) " +
                $"rows this command rewrites: -1 for unlimited (default) or 1-{CosmosDbMessageStoreOptions.MaxRetentionDays}. " +
                $"Overrides environment variable '{CommandRunner.UnresolvedRetentionEnvName}'. Must match the value the " +
                "NimBus hosts are configured with."
        };

        app.HelpOption(inherited: true);
        app.HelpTextGenerator = new ColoredHelpTextGenerator();

        Commands.InfraCommands.Register(app);
        Commands.TopologyCommands.Register(app, sbConnectionString);
        Commands.DeployCommands.Register(app);
        Commands.SetupCommand.Register(app);
        Commands.EndpointCommands.Register(app, sbConnectionString, dbConnectionString);
        Commands.ContainerCommands.Register(app, sbConnectionString, dbConnectionString, unresolvedRetentionDays);
        Commands.CatalogCommands.Register(app);
        Commands.AsyncApiCommands.Register(app);

        app.OnExecute(() =>
        {
            AnsiConsole.MarkupLine("[yellow]Specify a subcommand[/]");
            app.ShowHelp();
            return 1;
        });

        return app;
    }
}
