using System;
using Microsoft.Azure.Cosmos;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Inbox;
using NimBus.MessageStore.Abstractions;

namespace NimBus.MessageStore.CosmosDb.Tests;

internal static class CosmosDbStoreTestHarness
{
    private const string DatabaseId = "MessageDatabase";
    private const string ConnectionStringEnvironmentVariable = "NIMBUS_COSMOS_TEST_CONNECTION";
    private const string EndpointEnvironmentVariable = "NIMBUS_COSMOS_TEST_ENDPOINT";
    private const string KeyEnvironmentVariable = "NIMBUS_COSMOS_TEST_KEY";
    private const string GatewayModeEnvironmentVariable = "NIMBUS_COSMOS_TEST_GATEWAY";

    private static readonly Lazy<CosmosClient> Client = new(CreateClient);

    public static INimBusMessageStore CreateStore()
        => new CosmosDbClient(Client.Value);

    public static IInboxStore CreateInboxStore()
        => new CosmosInboxStore(new CosmosClientAdapter(Client.Value));

    private static CosmosClient CreateClient()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
        var endpoint = Environment.GetEnvironmentVariable(EndpointEnvironmentVariable);
        var key = Environment.GetEnvironmentVariable(KeyEnvironmentVariable);

        // The Cosmos emulator (Docker, vNext) only speaks Gateway mode; the SDK
        // default of Direct mode tries to reach partition addresses that aren't
        // exposed by the container. Opt in via NIMBUS_COSMOS_TEST_GATEWAY=1.
        var options = new CosmosClientOptions();
        var gateway = Environment.GetEnvironmentVariable(GatewayModeEnvironmentVariable);
        if (gateway is "1" or "true")
        {
            options.ConnectionMode = ConnectionMode.Gateway;
            options.LimitToEndpoint = true;
        }

        var client = !string.IsNullOrWhiteSpace(connectionString)
            ? new CosmosClient(connectionString, options)
            : !string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(key)
                ? new CosmosClient(endpoint, key, options)
                : null;

        if (client == null)
        {
            Assert.Inconclusive(
                $"{ConnectionStringEnvironmentVariable} or {EndpointEnvironmentVariable}/{KeyEnvironmentVariable} not set; skipping live Cosmos DB conformance suite.");
        }

        client!.CreateDatabaseIfNotExistsAsync(DatabaseId).GetAwaiter().GetResult();
        return client;
    }
}
