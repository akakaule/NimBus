using System;
using Microsoft.Azure.Cosmos;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;

namespace NimBus.MessageStore.CosmosDb.Tests;

internal static class CosmosDbStoreTestHarness
{
    private const string DatabaseId = "MessageDatabase";
    private const string ConnectionStringEnvironmentVariable = "NIMBUS_COSMOS_TEST_CONNECTION";
    private const string EndpointEnvironmentVariable = "NIMBUS_COSMOS_TEST_ENDPOINT";
    private const string KeyEnvironmentVariable = "NIMBUS_COSMOS_TEST_KEY";

    private static readonly Lazy<CosmosClient> Client = new(CreateClient);

    public static INimBusMessageStore CreateStore()
        => new CosmosDbClient(Client.Value);

    private static CosmosClient CreateClient()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
        var endpoint = Environment.GetEnvironmentVariable(EndpointEnvironmentVariable);
        var key = Environment.GetEnvironmentVariable(KeyEnvironmentVariable);

        var client = !string.IsNullOrWhiteSpace(connectionString)
            ? new CosmosClient(connectionString)
            : !string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(key)
                ? new CosmosClient(endpoint, key)
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
