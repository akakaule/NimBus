using System.Data.Common;
using System.Text.Json;

namespace NimBus.CommandLine;

internal sealed class InfrastructureDeployer
{
    private readonly CommandContext _context;
    private readonly IAzureCliRunner _az;

    public InfrastructureDeployer(CommandContext context, IAzureCliRunner az)
    {
        _context = context;
        _az = az;
    }

    public async Task ApplyAsync(InfrastructureOptions options, CancellationToken cancellationToken)
    {
        var names = NamingConventions.Build(options.SolutionId, options.Environment);

        await _az.EnsureLoggedInAsync(cancellationToken).ConfigureAwait(false);

        // Provider registration is subscription-scoped, so an RG-scoped pipeline
        // identity cannot perform it — and nothing in the bicep deploys Event Grid
        // resources (the provider only backs optional storage-hook webhooks).
        // Warn instead of failing the whole deployment.
        var eventGridRegistration = await _az.TryRunAsync(
            new[] { "provider", "register", "--namespace", "Microsoft.EventGrid" },
            cancellationToken).ConfigureAwait(false);
        if (!eventGridRegistration.Succeeded)
        {
            CliOutput.WriteLine("Warning: could not register the Microsoft.EventGrid provider (requires subscription-level permission). Pre-register it once per subscription if you plan to use Event Grid storage hooks.");
        }

        if (!string.IsNullOrWhiteSpace(options.ResourceNamePostFix))
        {
            CliOutput.WriteLine($"Ignoring --resource-name-postfix '{options.ResourceNamePostFix}' because the current bicep templates do not consume it.");
        }

        var existingLocations = await DiscoverExistingLocationsAsync(options.ResourceGroupName, cancellationToken).ConfigureAwait(false);
        var existingPlans = await DiscoverExistingPlansAsync(options.ResourceGroupName, cancellationToken).ConfigureAwait(false);

        // An existing deployment pins its plan choices (like the location pins):
        // EP <-> Flex cannot convert in place, and re-runs must not silently
        // rescale the management plan. Explicit CLI flags still win.
        existingPlans.TryGetValue(names.CoreAppServicePlanName, out var existingCorePlan);
        existingPlans.TryGetValue(names.ManagementAppServicePlanName, out var existingManagementPlan);

        var resolverPlan = PlanSelection.ResolveResolverPlan(options.ResolverPlan, existingCorePlan?.Tier);
        if (options.ResolverPlan is null && existingCorePlan is not null)
        {
            CliOutput.WriteLine($"Pinning resolver plan to the existing '{names.CoreAppServicePlanName}' plan type ({resolverPlan}).");
        }

        var managementPlanSku = PlanSelection.ResolveManagementPlanSku(options.ManagementPlanSku, existingManagementPlan?.SkuName, names.Environment);
        if (string.IsNullOrWhiteSpace(options.ManagementPlanSku) && existingManagementPlan is not null)
        {
            CliOutput.WriteLine($"Pinning management plan SKU to the existing '{names.ManagementAppServicePlanName}' SKU ({managementPlanSku}).");
        }

        CliOutput.WriteLine("Deploying core infrastructure...");
        await DeployCoreInfrastructureAsync(options, names, resolverPlan, managementPlanSku, existingLocations, cancellationToken).ConfigureAwait(false);

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
            PlanSelection.SupportsAlwaysOn(managementPlanSku),
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

    private async Task<Dictionary<string, ExistingAppServicePlan>> DiscoverExistingPlansAsync(string resourceGroupName, CancellationToken cancellationToken)
    {
        // The generic `az resource list` does not reliably populate sku, so ask the
        // Web resource provider directly; one call covers both NimBus plans.
        var plans = new Dictionary<string, ExistingAppServicePlan>(StringComparer.OrdinalIgnoreCase);
        var result = await _az.TryRunAsync(
            new[]
            {
                "appservice", "plan", "list",
                "--resource-group", resourceGroupName,
                "--query", "[].{name:name, skuName:sku.name, tier:sku.tier}",
                "--output", "json",
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return plans;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput);
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var name = element.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var skuName = element.TryGetProperty("skuName", out var skuElement) ? skuElement.GetString() : null;
                var tier = element.TryGetProperty("tier", out var tierElement) ? tierElement.GetString() : null;
                plans[name] = new ExistingAppServicePlan(skuName ?? string.Empty, tier ?? string.Empty);
            }
        }
        catch (JsonException)
        {
            // Resource group is empty or the response isn't JSON-shaped — proceed with no pins.
        }

        return plans;
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

    private async Task DeployCoreInfrastructureAsync(InfrastructureOptions options, DeploymentNames names, ResolverPlanChoice resolverPlan, string managementPlanSku, IReadOnlyDictionary<string, string> existingLocations, CancellationToken cancellationToken)
    {
        var storageProviderParam = options.StorageProvider == StorageProviderChoice.SqlServer ? "sqlserver" : "cosmos";
        var sqlModeParam = options.SqlMode == SqlProvisioningMode.External ? "external" : "provision";
        var resolverPlanParam = resolverPlan == ResolverPlanChoice.FlexConsumption ? "FlexConsumption" : "ElasticPremium";

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
            $"managementPlanSku={managementPlanSku}",
        };

        if (options.StorageProvider == StorageProviderChoice.SqlServer && options.SqlMode == SqlProvisioningMode.Provision)
        {
            arguments.Add($"sqlAdminLogin={options.SqlAdminLogin}");
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

        using var command = new AzureDeploymentCommand(arguments);
        if (options.StorageProvider == StorageProviderChoice.SqlServer && options.SqlMode == SqlProvisioningMode.Provision)
        {
            command.AddSecureParameter("sqlAdminPassword", options.SqlAdminPassword ?? string.Empty);
        }

        await _az.EnsureSuccessAsync(
            command.BuildArguments(),
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
        bool alwaysOnEnabled,
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
            $"appInsightsAppId={appInsightsAppId}",
            $"cosmosAccountEndpoint={cosmosAccountEndpoint}",
            $"serviceBusFullyQualifiedNamespace={serviceBusFullyQualifiedNamespace}",
            $"alwaysOnEnabled={(alwaysOnEnabled ? "true" : "false")}",
        };

        if (!string.IsNullOrWhiteSpace(options.IdentityAdminEmail))
        {
            arguments.Add($"identityAdminEmail={options.IdentityAdminEmail}");
        }

        if (!string.IsNullOrWhiteSpace(options.Location))
        {
            arguments.Add($"locationParam={options.Location}");
        }

        var pinned = new List<(string Name, string Location)>();
        AddPinnedLocation(arguments, existingLocations, names.WebAppName, "webAppLocation", pinned);
        AddPinnedLocation(arguments, existingLocations, names.ManagementAppServicePlanName, "managementAppServicePlanLocation", pinned);

        using var command = new AzureDeploymentCommand(arguments);
        command.AddSecureParameter("apiKey", appInsightsApiKey);
        command.AddSecureParameter("instrumentationKey", instrumentationKey);
        command.AddSecureParameter("sqlConnectionString", sqlConnectionString);
        if (!string.IsNullOrWhiteSpace(options.IdentityAdminEmail))
        {
            command.AddSecureParameter("identityAdminPassword", options.IdentityAdminPassword ?? string.Empty);
        }

        await _az.EnsureSuccessAsync(
            command.BuildArguments(),
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

    private sealed record ExistingAppServicePlan(string SkuName, string Tier);
}
