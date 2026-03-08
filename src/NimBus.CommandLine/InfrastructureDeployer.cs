namespace NimBus.CommandLine;

internal sealed class InfrastructureDeployer
{
    private const string CosmosDatabaseName = "MessageDatabase";
    private readonly CommandContext _context;
    private readonly AzureCliRunner _az;

    public InfrastructureDeployer(CommandContext context, AzureCliRunner az)
    {
        _context = context;
        _az = az;
    }

    public async Task ApplyAsync(InfrastructureOptions options, CancellationToken cancellationToken)
    {
        var names = NamingConventions.Build(options.SolutionId, options.Environment);

        await _az.EnsureLoggedInAsync(cancellationToken).ConfigureAwait(false);
        await _az.RegisterProviderAsync("Microsoft.EventGrid", cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(options.ResourceNamePostFix))
        {
            CliOutput.WriteLine($"Ignoring --resource-name-postfix '{options.ResourceNamePostFix}' because the current bicep templates do not consume it.");
        }

        CliOutput.WriteLine("Deploying core infrastructure...");
        await DeployCoreInfrastructureAsync(options, names, cancellationToken).ConfigureAwait(false);

        CliOutput.WriteLine("Preparing web app infrastructure inputs...");
        await _az.EnsureExtensionAsync("application-insights", cancellationToken).ConfigureAwait(false);

        var appInsightsApiKey = await EnsureAppInsightsApiKeyAsync(options.ResourceGroupName, names.AppInsightsName, cancellationToken).ConfigureAwait(false);
        var cosmosConnectionString = await GetCosmosConnectionStringAsync(options.ResourceGroupName, names.CosmosAccountName, cancellationToken).ConfigureAwait(false);
        var managerConnectionString = await GetServiceBusConnectionStringAsync(options.ResourceGroupName, names.ServiceBusNamespace, cancellationToken).ConfigureAwait(false);
        var appInsightsAppId = await _az.CaptureValueAsync(
            new[]
            {
                "monitor", "app-insights", "component", "show",
                "--app", names.AppInsightsName,
                "--resource-group", options.ResourceGroupName,
                "--query", "appId",
                "--output", "tsv",
            },
            cancellationToken,
            $"Failed to read Application Insights app id for '{names.AppInsightsName}'.").ConfigureAwait(false);

        var instrumentationKey = await _az.CaptureValueAsync(
            new[]
            {
                "monitor", "app-insights", "component", "show",
                "--app", names.AppInsightsName,
                "--resource-group", options.ResourceGroupName,
                "--query", "instrumentationKey",
                "--output", "tsv",
            },
            cancellationToken,
            $"Failed to read Application Insights instrumentation key for '{names.AppInsightsName}'.").ConfigureAwait(false);

        CliOutput.WriteLine("Deploying web app infrastructure...");
        await DeployWebAppInfrastructureAsync(
            options,
            names,
            appInsightsApiKey,
            appInsightsAppId,
            instrumentationKey,
            cosmosConnectionString,
            managerConnectionString,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task DeployCoreInfrastructureAsync(InfrastructureOptions options, DeploymentNames names, CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "deployment", "group", "create",
            "--resource-group", options.ResourceGroupName,
            "--template-file", _context.CoreBicepPath,
            "--parameters",
            $"solutionId={names.SolutionId}",
            $"environment={names.Environment}",
            $"resolverId={NimBus.Core.Messages.Constants.ResolverId}",
            $"uniqueDeploy={Guid.NewGuid():N}",
        };

        if (!string.IsNullOrWhiteSpace(options.Location))
        {
            arguments.Add($"locationParam={options.Location}");
        }

        await _az.EnsureSuccessAsync(
            arguments,
            _context.DeployDirectory,
            cancellationToken,
            "Core infrastructure deployment failed.").ConfigureAwait(false);
    }

    private async Task DeployWebAppInfrastructureAsync(
        InfrastructureOptions options,
        DeploymentNames names,
        string appInsightsApiKey,
        string appInsightsAppId,
        string instrumentationKey,
        string cosmosConnectionString,
        string managerConnectionString,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "deployment", "group", "create",
            "--resource-group", options.ResourceGroupName,
            "--template-file", _context.WebAppBicepPath,
            "--parameters",
            $"solutionId={names.SolutionId}",
            $"environment={names.Environment}",
            $"webAppVersion={options.WebAppVersion}",
            $"apiKey={appInsightsApiKey}",
            $"appInsightsAppId={appInsightsAppId}",
            $"instrumentationKey={instrumentationKey}",
            $"cosmosDbConnectionString={cosmosConnectionString}",
            $"managerServiceBusConnection={managerConnectionString}",
        };

        if (!string.IsNullOrWhiteSpace(options.Location))
        {
            arguments.Add($"locationParam={options.Location}");
        }

        await _az.EnsureSuccessAsync(
            arguments,
            _context.DeployDirectory,
            cancellationToken,
            "Web app infrastructure deployment failed.").ConfigureAwait(false);
    }

    private async Task<string> EnsureAppInsightsApiKeyAsync(string resourceGroupName, string appInsightsName, CancellationToken cancellationToken)
    {
        var existingKey = await _az.TryRunAsync(
            new[]
            {
                "monitor", "app-insights", "api-key", "show",
                "--app", appInsightsName,
                "--resource-group", resourceGroupName,
                "--api-key", "management-app",
            },
            cancellationToken).ConfigureAwait(false);

        if (existingKey.Succeeded && !string.IsNullOrWhiteSpace(existingKey.StandardOutput) && !existingKey.StandardOutput.TrimStart().StartsWith("[]", StringComparison.Ordinal))
        {
            await _az.EnsureSuccessAsync(
                new[]
                {
                    "monitor", "app-insights", "api-key", "delete",
                    "--app", appInsightsName,
                    "--resource-group", resourceGroupName,
                    "--api-key", "management-app",
                },
                cancellationToken,
                $"Failed to delete the existing Application Insights API key for '{appInsightsName}'.").ConfigureAwait(false);
        }

        return await _az.CaptureValueAsync(
            new[]
            {
                "monitor", "app-insights", "api-key", "create",
                "--app", appInsightsName,
                "--resource-group", resourceGroupName,
                "--api-key", "management-app",
                "--read-properties", "ReadTelemetry",
                "--write-properties", "WriteAnnotations",
                "--query", "apiKey",
                "--output", "tsv",
            },
            cancellationToken,
            $"Failed to create the Application Insights API key for '{appInsightsName}'.").ConfigureAwait(false);
    }

    private async Task<string> GetCosmosConnectionStringAsync(string resourceGroupName, string cosmosAccountName, CancellationToken cancellationToken)
    {
        var exists = await _az.CaptureValueAsync(
            new[]
            {
                "cosmosdb", "sql", "database", "exists",
                "--resource-group", resourceGroupName,
                "--account-name", cosmosAccountName,
                "--name", CosmosDatabaseName,
                "--output", "tsv",
            },
            cancellationToken,
            $"Failed to verify the '{CosmosDatabaseName}' Cosmos database.").ConfigureAwait(false);

        if (!string.Equals(exists, "true", StringComparison.OrdinalIgnoreCase))
        {
            return "Placeholder";
        }

        return await _az.CaptureValueAsync(
            new[]
            {
                "cosmosdb", "keys", "list",
                "--resource-group", resourceGroupName,
                "--name", cosmosAccountName,
                "--type", "connection-strings",
                "--query", "connectionStrings[0].connectionString",
                "--output", "tsv",
            },
            cancellationToken,
            $"Failed to read the Cosmos DB connection string for '{cosmosAccountName}'.").ConfigureAwait(false);
    }

    private Task<string> GetServiceBusConnectionStringAsync(string resourceGroupName, string namespaceName, CancellationToken cancellationToken) =>
        _az.CaptureValueAsync(
            new[]
            {
                "servicebus", "namespace", "authorization-rule", "keys", "list",
                "--resource-group", resourceGroupName,
                "--namespace-name", namespaceName,
                "--name", "RootManageSharedAccessKey",
                "--query", "primaryConnectionString",
                "--output", "tsv",
            },
            cancellationToken,
            $"Failed to read the Service Bus connection string for '{namespaceName}'.");
}
