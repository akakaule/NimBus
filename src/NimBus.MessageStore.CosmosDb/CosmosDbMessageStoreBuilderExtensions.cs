using System;
using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NimBus.Core.Extensions;
using NimBus.MessageStore.Abstractions;
using NimBus.OpenTelemetry;

namespace NimBus.MessageStore;

/// <summary>
/// Provider-aware registration for the Cosmos DB-backed message store. This is the
/// single entry point consumers should call when running NimBus with Cosmos. Registers
/// the four storage contracts, the storage-provider marker (consumed by builder
/// validation), and provider capabilities.
/// </summary>
public static class CosmosDbMessageStoreBuilderExtensions
{
    /// <summary>
    /// Registers the Cosmos DB message store as the active NimBus storage provider.
    /// Reads the connection from configuration: <c>CosmosAccountEndpoint</c>,
    /// connection string named <c>"cosmos"</c>, or <c>CosmosConnection</c>, in that
    /// order. AAD is used when the endpoint does not contain <c>AccountKey=</c>.
    /// </summary>
    public static INimBusBuilder AddCosmosDbMessageStore(this INimBusBuilder builder)
    {
        var services = builder.Services;

        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            return CreateCosmosClient(config);
        });

        services.AddSingleton<ICosmosDbClient>(sp =>
        {
            var cosmosClient = sp.GetRequiredService<CosmosClient>();
            return new CosmosDbClient(cosmosClient);
        });

        RegisterContracts(services);
        return builder;
    }

    /// <summary>
    /// Registers the Cosmos DB message store using a pre-constructed CosmosClient
    /// (useful for tests and advanced scenarios).
    /// </summary>
    public static INimBusBuilder AddCosmosDbMessageStore(this INimBusBuilder builder, CosmosClient cosmosClient)
    {
        ArgumentNullException.ThrowIfNull(cosmosClient);
        builder.Services.AddSingleton(cosmosClient);
        builder.Services.AddSingleton<ICosmosDbClient>(sp => new CosmosDbClient(cosmosClient));
        RegisterContracts(builder.Services);
        return builder;
    }

    private static void RegisterContracts(IServiceCollection services)
    {
        services.AddSingleton<INimBusMessageStore>(sp => (CosmosDbClient)sp.GetRequiredService<ICosmosDbClient>());
        services.AddSingleton<IMessageTrackingStore>(sp =>
            NimBusOpenTelemetryDecorators.InstrumentMessageTrackingStore(
                sp.GetRequiredService<INimBusMessageStore>(),
                "cosmos",
                sp.GetService<IOptionsMonitor<NimBusOpenTelemetryOptions>>()));
        services.AddSingleton<ISubscriptionStore>(sp => sp.GetRequiredService<INimBusMessageStore>());
        services.AddSingleton<IEndpointMetadataStore>(sp => sp.GetRequiredService<INimBusMessageStore>());
        services.AddSingleton<IMetricsStore>(sp => sp.GetRequiredService<INimBusMessageStore>());
        services.AddSingleton<IStorageProviderRegistration>(_ => new CosmosDbStorageProviderRegistration());
        services.AddSingleton<IStorageProviderCapabilities>(_ => new CosmosDbStorageProviderCapabilities());
    }

    internal static CosmosClient CreateCosmosClient(IConfiguration config)
    {
        // Treat empty strings as missing — appsettings.json may declare an empty
        // default like "CosmosAccountEndpoint": "" which would otherwise short-
        // circuit the null-coalescing fallback chain and pass "" to CosmosClient.
        var endpoint = NullIfEmpty(config.GetValue<string>("CosmosAccountEndpoint"));
        var connStr = NullIfEmpty(config.GetConnectionString("cosmos"));
        var connFallback = NullIfEmpty(config.GetValue<string>("CosmosConnection"));

        if (endpoint is not null && !endpoint.Contains("AccountKey=", StringComparison.OrdinalIgnoreCase))
        {
            return new CosmosClient(endpoint, new DefaultAzureCredential());
        }

        var connectionString = endpoint
            ?? connStr
            ?? connFallback
            ?? throw new InvalidOperationException(
                "Cosmos DB configuration is required. Set 'CosmosAccountEndpoint', the 'cosmos' connection string, or 'CosmosConnection'.");
        return new CosmosClient(connectionString);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}

internal sealed class CosmosDbStorageProviderRegistration : IStorageProviderRegistration
{
    public string ProviderName => "Cosmos DB";
}

internal sealed class CosmosDbStorageProviderCapabilities : IStorageProviderCapabilities
{
    public bool SupportsCrossAccountCopy => true;
}
