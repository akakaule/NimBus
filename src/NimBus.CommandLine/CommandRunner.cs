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

    public static async Task Run(CommandOption sbConnectionString, CommandOption dbConnectionString, Func<ServiceBusClient, CosmosDbClient, ServiceBusAdministrationClient, Task> func)
    {
        var sbConnStr = sbConnectionString.HasValue() ? sbConnectionString.Value() : Environment.GetEnvironmentVariable(SbConnectionStringEnvName);
        var dbConnStr = dbConnectionString.HasValue() ? dbConnectionString.Value() : Environment.GetEnvironmentVariable(DbConnectionStringEnvName);

        var serviceBusClient = new ServiceBusClient(sbConnStr);
        var serviceBusAdmin = new ServiceBusAdministrationClient(sbConnStr);
        var cosmosClient = new CosmosClient(dbConnStr);
        var cosmosDbClient = new CosmosDbClient(cosmosClient);

        await func(serviceBusClient, cosmosDbClient, serviceBusAdmin);
    }

    public static async Task Run(CommandOption sbConnectionString, CommandOption dbConnectionString, Func<ServiceBusClient, CosmosDbClient, Task> func)
    {
        var sbConnStr = sbConnectionString.HasValue() ? sbConnectionString.Value() : Environment.GetEnvironmentVariable(SbConnectionStringEnvName);
        var dbConnStr = dbConnectionString.HasValue() ? dbConnectionString.Value() : Environment.GetEnvironmentVariable(DbConnectionStringEnvName);

        var serviceBusClient = new ServiceBusClient(sbConnStr);
        var cosmosClient = new CosmosClient(dbConnStr);
        var cosmosDbClient = new CosmosDbClient(cosmosClient);

        await func(serviceBusClient, cosmosDbClient);
    }

    public static async Task Run(CommandOption dbConnectionString, Func<CosmosDbClient, Task> func)
    {
        var dbConnStr = dbConnectionString.HasValue() ? dbConnectionString.Value() : Environment.GetEnvironmentVariable(DbConnectionStringEnvName);

        var cosmosClient = new CosmosClient(dbConnStr);
        var cosmosDbClient = new CosmosDbClient(cosmosClient);

        await func(cosmosDbClient);
    }

    public static async Task Run(CommandOption sbConnectionString, Func<ServiceBusAdministrationClient, Task> func)
    {
        var sbConnStr = sbConnectionString.HasValue() ? sbConnectionString.Value() : Environment.GetEnvironmentVariable(SbConnectionStringEnvName);

        var serviceBusAdmin = new ServiceBusAdministrationClient(sbConnStr);

        await func(serviceBusAdmin);
    }

    public static async Task Run(CommandOption sbConnectionString, Func<ServiceBusClient, Task> func)
    {
        var sbConnStr = sbConnectionString.HasValue() ? sbConnectionString.Value() : Environment.GetEnvironmentVariable(SbConnectionStringEnvName);

        var serviceBusClient = new ServiceBusClient(sbConnStr);

        await func(serviceBusClient);
    }

    public static async Task Run(CommandOption sourceDbConnectionString, CommandOption targetDbConnectionString, Func<CosmosClient, CosmosClient, Task> func)
    {
        var sourceConnStr = sourceDbConnectionString.HasValue() ? sourceDbConnectionString.Value() : Environment.GetEnvironmentVariable(DbConnectionStringEnvName);
        var targetConnStr = targetDbConnectionString.HasValue() ? targetDbConnectionString.Value() : null;

        if (string.IsNullOrEmpty(sourceConnStr))
            throw new InvalidOperationException($"Source Cosmos DB connection string is required. Use -dbc or set environment variable '{DbConnectionStringEnvName}'.");
        if (string.IsNullOrEmpty(targetConnStr))
            throw new InvalidOperationException("Target Cosmos DB connection string is required. Use --target-dbc.");

        using var sourceClient = new CosmosClient(sourceConnStr);
        using var targetClient = new CosmosClient(targetConnStr);

        await func(sourceClient, targetClient);
    }
}
