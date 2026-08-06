using System.Globalization;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Azure.Cosmos;
using NimBus.MessageStore;

namespace NimBus.CommandLine;

static class CommandRunner
{
    public const string SbConnectionStringEnvName = "AzureServiceBus_ConnectionString";
    public const string DbConnectionStringEnvName = "CosmosDb_ConnectionString";

    /// <summary>Environment form of the host configuration key <c>NimBus:Cosmos:UnresolvedRetentionDays</c>,
    /// so one value configures the hosts and the CLI alike.</summary>
    public const string UnresolvedRetentionEnvName = "NimBus__Cosmos__UnresolvedRetentionDays";

    /// <summary>
    /// Builds a ServiceBusClient from either a connection string or a fully
    /// qualified namespace (e.g. mybus.servicebus.windows.net). Values without
    /// a shared access key are treated as a namespace and authenticate with
    /// Entra ID via DefaultAzureCredential — the same heuristic the WebApp and
    /// Resolver use, so operators can avoid distributing connection strings.
    /// </summary>
    internal static ServiceBusClient CreateServiceBusClient(string? value)
    {
        var resolved = RequireServiceBusValue(value);
        return IsServiceBusConnectionString(resolved)
            ? new ServiceBusClient(resolved)
            : new ServiceBusClient(resolved, new DefaultAzureCredential());
    }

    /// <summary>
    /// Same connection-string-or-namespace handling as <see cref="CreateServiceBusClient"/>.
    /// </summary>
    internal static ServiceBusAdministrationClient CreateServiceBusAdministrationClient(string? value)
    {
        var resolved = RequireServiceBusValue(value);
        return IsServiceBusConnectionString(resolved)
            ? new ServiceBusAdministrationClient(resolved)
            : new ServiceBusAdministrationClient(resolved, new DefaultAzureCredential());
    }

    /// <summary>
    /// Builds a CosmosClient from either a connection string or an account
    /// endpoint URI (e.g. https://myaccount.documents.azure.com/). Values
    /// without an AccountKey are treated as an endpoint and authenticate with
    /// Entra ID via DefaultAzureCredential — mirrors the Cosmos message store
    /// provider's heuristic.
    /// </summary>
    internal static CosmosClient CreateCosmosClient(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Cosmos DB connection is required. Use -dbc or set environment variable '{DbConnectionStringEnvName}'. " +
                "Pass a connection string, or an account endpoint URI (e.g. https://myaccount.documents.azure.com/) to authenticate with Entra ID (DefaultAzureCredential).");
        }

        return value.Contains("AccountKey=", StringComparison.OrdinalIgnoreCase)
            ? new CosmosClient(value)
            : new CosmosClient(value, new DefaultAzureCredential());
    }

    private static bool IsServiceBusConnectionString(string value) =>
        value.Contains("SharedAccessKey=", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("SharedAccessSignature=", StringComparison.OrdinalIgnoreCase);

    private static string RequireServiceBusValue(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException(
                $"Service Bus connection is required. Use -sbc or set environment variable '{SbConnectionStringEnvName}'. " +
                "Pass a connection string, or a fully qualified namespace (e.g. mybus.servicebus.windows.net) to authenticate with Entra ID (DefaultAzureCredential).")
            : value;

    private static string? Resolve(CommandOption option, string envName) =>
        option.HasValue() ? option.Value() : Environment.GetEnvironmentVariable(envName);

    /// <summary>
    /// Resubmission rewrites the whole tracking document, so the CLI must stamp the same
    /// retention the hosts are configured with; otherwise `nb container resubmit` silently
    /// disables expiry on the rows it touches.
    /// </summary>
    internal static CosmosDbMessageStoreOptions ResolveStoreOptions(CommandOption? unresolvedRetentionDays)
    {
        var raw = unresolvedRetentionDays?.HasValue() == true
            ? unresolvedRetentionDays.Value()
            : Environment.GetEnvironmentVariable(UnresolvedRetentionEnvName);

        var options = new CosmosDbMessageStoreOptions();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
            {
                throw new InvalidOperationException(
                    $"--unresolved-retention-days (or {UnresolvedRetentionEnvName}) must be a whole number; was '{raw}'.");
            }

            options.UnresolvedRetentionDays = days;
        }

        options.Validate();
        return options;
    }

    private static CosmosDbClient CreateCosmosDbClient(string? dbConnStr, CommandOption? unresolvedRetentionDays) =>
        new(CreateCosmosClient(dbConnStr), logger: null, ResolveStoreOptions(unresolvedRetentionDays));

    public static async Task Run(CommandOption sbConnectionString, CommandOption dbConnectionString, Func<ServiceBusClient, CosmosDbClient, ServiceBusAdministrationClient, Task> func, CommandOption? unresolvedRetentionDays = null)
    {
        var sbConnStr = Resolve(sbConnectionString, SbConnectionStringEnvName);
        var dbConnStr = Resolve(dbConnectionString, DbConnectionStringEnvName);

        var serviceBusClient = CreateServiceBusClient(sbConnStr);
        var serviceBusAdmin = CreateServiceBusAdministrationClient(sbConnStr);
        var cosmosDbClient = CreateCosmosDbClient(dbConnStr, unresolvedRetentionDays);

        await func(serviceBusClient, cosmosDbClient, serviceBusAdmin);
    }

    public static async Task Run(CommandOption sbConnectionString, CommandOption dbConnectionString, Func<ServiceBusClient, CosmosDbClient, Task> func, CommandOption? unresolvedRetentionDays = null)
    {
        var sbConnStr = Resolve(sbConnectionString, SbConnectionStringEnvName);
        var dbConnStr = Resolve(dbConnectionString, DbConnectionStringEnvName);

        var serviceBusClient = CreateServiceBusClient(sbConnStr);
        var cosmosDbClient = CreateCosmosDbClient(dbConnStr, unresolvedRetentionDays);

        await func(serviceBusClient, cosmosDbClient);
    }

    public static async Task Run(CommandOption dbConnectionString, Func<CosmosDbClient, Task> func, CommandOption? unresolvedRetentionDays = null)
    {
        var dbConnStr = Resolve(dbConnectionString, DbConnectionStringEnvName);

        var cosmosDbClient = CreateCosmosDbClient(dbConnStr, unresolvedRetentionDays);

        await func(cosmosDbClient);
    }

    public static async Task Run(CommandOption sbConnectionString, Func<ServiceBusAdministrationClient, Task> func)
    {
        var sbConnStr = Resolve(sbConnectionString, SbConnectionStringEnvName);

        var serviceBusAdmin = CreateServiceBusAdministrationClient(sbConnStr);

        await func(serviceBusAdmin);
    }

    public static async Task Run(CommandOption sbConnectionString, Func<ServiceBusClient, Task> func)
    {
        var sbConnStr = Resolve(sbConnectionString, SbConnectionStringEnvName);

        var serviceBusClient = CreateServiceBusClient(sbConnStr);

        await func(serviceBusClient);
    }

    public static async Task Run(CommandOption sourceDbConnectionString, CommandOption targetDbConnectionString, Func<CosmosClient, CosmosClient, Task> func)
    {
        var sourceConnStr = Resolve(sourceDbConnectionString, DbConnectionStringEnvName);
        var targetConnStr = targetDbConnectionString.HasValue() ? targetDbConnectionString.Value() : null;

        if (string.IsNullOrEmpty(sourceConnStr))
            throw new InvalidOperationException($"Source Cosmos DB connection string is required. Use -dbc or set environment variable '{DbConnectionStringEnvName}'.");
        if (string.IsNullOrEmpty(targetConnStr))
            throw new InvalidOperationException("Target Cosmos DB connection string is required. Use --target-dbc.");

        using var sourceClient = CreateCosmosClient(sourceConnStr);
        using var targetClient = CreateCosmosClient(targetConnStr);

        await func(sourceClient, targetClient);
    }
}
