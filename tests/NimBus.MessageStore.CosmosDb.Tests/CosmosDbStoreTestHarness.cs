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
    private const string RequiredEnvironmentVariable = "NIMBUS_COSMOS_TEST_REQUIRED";

    private static readonly Lazy<CosmosClient> Client = new(CreateClient);

    /// <summary>The raw client, for assertions the store contracts cannot express — such as
    /// reading back a container's <see cref="ContainerProperties.DefaultTimeToLive"/>.</summary>
    public static CosmosClient RawClient => Client.Value;

    public static INimBusMessageStore CreateStore()
        => new CosmosDbClient(Client.Value);

    public static INimBusMessageStore CreateStore(CosmosDbMessageStoreOptions options)
        => new CosmosDbClient(Client.Value, null, options);

    public static IInboxStore CreateInboxStore()
        // The emulator only offers Session consistency, so the live suite acknowledges the
        // relaxed level; the strong-consistency startup gate itself is covered by the fake
        // adapter tests in CosmosInboxTests.
        => new CosmosInboxStore(
            new CosmosClientAdapter(Client.Value),
            new CosmosInboxOptions { AllowRelaxedConsistency = true });

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
            // A suite that can silently skip cannot establish that the provider conforms.
            // CI sets NIMBUS_COSMOS_TEST_REQUIRED so a missing emulator fails the run
            // instead of quietly reporting green; a developer without Docker still skips.
            var required = Environment.GetEnvironmentVariable(RequiredEnvironmentVariable);
            if (required is "1" or "true")
            {
                Assert.Fail(
                    $"{RequiredEnvironmentVariable} is set but no Cosmos endpoint is configured: set " +
                    $"{ConnectionStringEnvironmentVariable}, or {EndpointEnvironmentVariable} and {KeyEnvironmentVariable}.");
            }

            Assert.Inconclusive(
                $"{ConnectionStringEnvironmentVariable} or {EndpointEnvironmentVariable}/{KeyEnvironmentVariable} not set; skipping live Cosmos DB conformance suite.");
        }

        client!.CreateDatabaseIfNotExistsAsync(DatabaseId).GetAwaiter().GetResult();
        return client;
    }
}
