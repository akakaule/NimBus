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

    public static async Task Run(CommandOption sbConnectionString, CommandOption dbConnectionString, Func<ServiceBusClient, CosmosDbClient, ServiceBusAdministrationClient, Task> func)
    {
        var sbConnStr = Resolve(sbConnectionString, SbConnectionStringEnvName);
        var dbConnStr = Resolve(dbConnectionString, DbConnectionStringEnvName);

        var serviceBusClient = CreateServiceBusClient(sbConnStr);
        var serviceBusAdmin = CreateServiceBusAdministrationClient(sbConnStr);
        var cosmosClient = CreateCosmosClient(dbConnStr);
        var cosmosDbClient = new CosmosDbClient(cosmosClient);

        await func(serviceBusClient, cosmosDbClient, serviceBusAdmin);
    }

    public static async Task Run(CommandOption sbConnectionString, CommandOption dbConnectionString, Func<ServiceBusClient, CosmosDbClient, Task> func)
    {
        var sbConnStr = Resolve(sbConnectionString, SbConnectionStringEnvName);
        var dbConnStr = Resolve(dbConnectionString, DbConnectionStringEnvName);

        var serviceBusClient = CreateServiceBusClient(sbConnStr);
        var cosmosClient = CreateCosmosClient(dbConnStr);
        var cosmosDbClient = new CosmosDbClient(cosmosClient);

        await func(serviceBusClient, cosmosDbClient);
    }

    public static async Task Run(CommandOption dbConnectionString, Func<CosmosDbClient, Task> func)
    {
        var dbConnStr = Resolve(dbConnectionString, DbConnectionStringEnvName);

        var cosmosClient = CreateCosmosClient(dbConnStr);
        var cosmosDbClient = new CosmosDbClient(cosmosClient);

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
