using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NimBus.Core.Inbox;

namespace NimBus.MessageStore;

/// <summary>
/// Dependency-injection registration for the Cosmos DB inbox store.
/// </summary>
public static class CosmosInboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers a Cosmos inbox using the <see cref="CosmosClient"/> already in the container.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional inbox container configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddNimBusCosmosInbox(
        this IServiceCollection services,
        Action<CosmosInboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        return Register(
            services,
            configure,
            serviceProvider => serviceProvider.GetRequiredService<CosmosClient>());
    }

    /// <summary>
    /// Registers a Cosmos inbox using a caller-supplied SDK client.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="cosmosClient">The shared Cosmos SDK client.</param>
    /// <param name="configure">Optional inbox container configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddNimBusCosmosInbox(
        this IServiceCollection services,
        CosmosClient cosmosClient,
        Action<CosmosInboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(cosmosClient);
        return Register(services, configure, _ => cosmosClient);
    }

    private static IServiceCollection Register(
        IServiceCollection services,
        Action<CosmosInboxOptions>? configure,
        Func<IServiceProvider, CosmosClient> getClient)
    {
        var options = new CosmosInboxOptions();
        configure?.Invoke(options);
        options.Validate();

        services.TryAddSingleton<CosmosInboxStore>(serviceProvider =>
            new CosmosInboxStore(
                new CosmosClientAdapter(getClient(serviceProvider)),
                options,
                serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System));
        services.TryAddSingleton<IInboxStore>(serviceProvider =>
            serviceProvider.GetRequiredService<CosmosInboxStore>());
        services.TryAddKeyedSingleton<IInboxStore>(InboxStore.Cosmos, (serviceProvider, _) =>
            serviceProvider.GetRequiredService<CosmosInboxStore>());

        return services;
    }
}
