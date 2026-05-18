using System.Data.Common;
using System.Text.Json;

namespace NimBus.CommandLine;

internal sealed class InfrastructureDeployer
{
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

        var existingLocations = await DiscoverExistingLocationsAsync(options.ResourceGroupName, cancellationToken).ConfigureAwait(false);

        CliOutput.WriteLine("Deploying core infrastructure...");
        await DeployCoreInfrastructureAsync(options, names, existingLocations, cancellationToken).ConfigureAwait(false);

        CliOutput.WriteLine("Preparing web app infrastructure inputs...");
        await _az.EnsureExtensionAsync("application-insights", cancellationToken).ConfigureAwait(false);

        var appInsightsApiKey = await EnsureAppInsightsApiKeyAsync(options.ResourceGroupName, names.AppInsightsName, cancellationToken).ConfigureAwait(false);
        var cosmosAccountEndpoint = options.StorageProvider == StorageProviderChoice.Cosmos
            ? await GetCosmosAccountEndpointAsync(options.ResourceGroupName, names.CosmosAccountName, cancellationToken).ConfigureAwait(false)
            : string.Empty;
        var sqlConnectionString = ResolveSqlConnectionString(options, names);
        var serviceBusFullyQualifiedNamespace = GetServiceBusFullyQualifiedNamespace(names.ServiceBusNamespace);
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
            cosmosAccountEndpoint,
            sqlConnectionString,
            serviceBusFullyQualifiedNamespace,
            existingLocations,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, string>> DiscoverExistingLocationsAsync(string resourceGroupName, CancellationToken cancellationToken)
    {
        var locations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = await _az.TryRunAsync(
            new[]
            {
                "resource", "list",
                "--resource-group", resourceGroupName,
                "--query", "[].{name:name, location:location}",
                "--output", "json",
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return locations;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput);
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("name", out var nameElement) || !element.TryGetProperty("location", out var locationElement))
                {
                    continue;
                }

                var name = nameElement.GetString();
                var location = locationElement.GetString();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(location))
                {
                    continue;
                }

                locations[name] = location;
            }
        }
        catch (JsonException)
        {
            // Resource group is empty or the response isn't JSON-shaped — proceed with no pins.
        }

        return locations;
    }

    private static void AddPinnedLocation(List<string> arguments, IReadOnlyDictionary<string, string> existingLocations, string resourceName, string bicepParamName, List<(string Name, string Location)> pinned)
    {
        if (existingLocations.TryGetValue(resourceName, out var location) && !string.IsNullOrWhiteSpace(location))
        {
            arguments.Add($"{bicepParamName}={location}");
            pinned.Add((resourceName, location));
        }
    }

    private static string ResolveSqlConnectionString(InfrastructureOptions options, DeploymentNames names)
    {
        if (options.StorageProvider != StorageProviderChoice.SqlServer) return string.Empty;
        if (options.SqlMode == SqlProvisioningMode.External)
        {
            return options.SqlConnectionString ?? string.Empty;
        }

        var sqlServerName = EffectiveSqlServerName(options, names);
        var builder = new DbConnectionStringBuilder
        {
            ["Server"] = $"tcp:{sqlServerName}.database.windows.net,1433",
            ["Initial Catalog"] = "MessageDatabase",
            ["User ID"] = options.SqlAdminLogin ?? string.Empty,
            ["Password"] = options.SqlAdminPassword ?? string.Empty,
            ["Encrypt"] = true,
        };

        return builder.ConnectionString;
    }

    private static string EffectiveSqlServerName(InfrastructureOptions options, DeploymentNames names) =>
        string.IsNullOrWhiteSpace(options.SqlServerName)
            ? names.SqlServerName
            : options.SqlServerName!.ToLowerInvariant();

    private async Task DeployCoreInfrastructureAsync(InfrastructureOptions options, DeploymentNames names, IReadOnlyDictionary<string, string> existingLocations, CancellationToken cancellationToken)
    {
        var storageProviderParam = options.StorageProvider == StorageProviderChoice.SqlServer ? "sqlserver" : "cosmos";
        var sqlModeParam = options.SqlMode == SqlProvisioningMode.External ? "external" : "provision";
        var resolverPlanParam = options.ResolverPlan == ResolverPlanChoice.FlexConsumption ? "FlexConsumption" : "ElasticPremium";

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
            $"storageProvider={storageProviderParam}",
            $"sqlMode={sqlModeParam}",
            $"resolverPlan={resolverPlanParam}",
        };

        if (options.StorageProvider == StorageProviderChoice.SqlServer && options.SqlMode == SqlProvisioningMode.Provision)
        {
            arguments.Add($"sqlAdminLogin={options.SqlAdminLogin}");
            arguments.Add($"sqlAdminPassword={options.SqlAdminPassword}");
        }

        if (!string.IsNullOrWhiteSpace(options.SqlServerName))
        {
            arguments.Add($"sqlServerName={EffectiveSqlServerName(options, names)}");
        }

        if (!string.IsNullOrWhiteSpace(options.Location))
        {
            arguments.Add($"locationParam={options.Location}");
        }

        var pinned = new List<(string Name, string Location)>();
        AddPinnedLocation(arguments, existingLocations, names.ServiceBusNamespace, "serviceBusLocation", pinned);
        AddPinnedLocation(arguments, existingLocations, names.AppInsightsName, "appInsightsLocation", pinned);
        AddPinnedLocation(arguments, existingLocations, names.CosmosAccountName, "cosmosLocation", pinned);
        AddPinnedLocation(arguments, existingLocations, EffectiveSqlServerName(options, names), "sqlLocation", pinned);
        AddPinnedLocation(arguments, existingLocations, names.FuncStorageAccountName, "funcStorageLocation", pinned);
        AddPinnedLocation(arguments, existingLocations, names.ManagementAppServicePlanName, "managementAppServicePlanLocation", pinned);
        AddPinnedLocation(arguments, existingLocations, names.CoreAppServicePlanName, "coreAppServicePlanLocation", pinned);
        AddPinnedLocation(arguments, existingLocations, names.ResolverFunctionAppName, "resolverFunctionAppLocation", pinned);

        if (pinned.Count > 0)
        {
            CliOutput.WriteLine($"Pinning {pinned.Count} existing resource(s) to their current location:");
            foreach (var (name, location) in pinned)
            {
                CliOutput.WriteLine($"  {name} → {location}");
            }
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
        string cosmosAccountEndpoint,
        string sqlConnectionString,
        string serviceBusFullyQualifiedNamespace,
        IReadOnlyDictionary<string, string> existingLocations,
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
            $"cosmosAccountEndpoint={cosmosAccountEndpoint}",
            $"sqlConnectionString={sqlConnectionString}",
            $"serviceBusFullyQualifiedNamespace={serviceBusFullyQualifiedNamespace}",
        };

        if (!string.IsNullOrWhiteSpace(options.Location))
        {
            arguments.Add($"locationParam={options.Location}");
        }

        var pinned = new List<(string Name, string Location)>();
        AddPinnedLocation(arguments, existingLocations, names.WebAppName, "webAppLocation", pinned);
        AddPinnedLocation(arguments, existingLocations, names.ManagementAppServicePlanName, "managementAppServicePlanLocation", pinned);

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

    private Task<string> GetCosmosAccountEndpointAsync(string resourceGroupName, string cosmosAccountName, CancellationToken cancellationToken) =>
        _az.CaptureValueAsync(
            new[]
            {
                "cosmosdb", "show",
                "--resource-group", resourceGroupName,
                "--name", cosmosAccountName,
                "--query", "documentEndpoint",
                "--output", "tsv",
            },
            cancellationToken,
            $"Failed to read the Cosmos DB endpoint for '{cosmosAccountName}'.");

    private static string GetServiceBusFullyQualifiedNamespace(string namespaceName) =>
        $"{namespaceName}.servicebus.windows.net";
}
