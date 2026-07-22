using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NimBus.Core.Inbox;

namespace NimBus.Testing;

/// <summary>
/// Dependency-injection registration for the in-memory inbox store.
/// </summary>
public static class InMemoryInboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers one in-memory inbox singleton as both the default and keyed inbox store.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddNimBusInMemoryInbox(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<InMemoryInboxStore>(serviceProvider =>
            new InMemoryInboxStore(serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System));
        services.TryAddSingleton<IInboxStore>(serviceProvider =>
            serviceProvider.GetRequiredService<InMemoryInboxStore>());
        services.TryAddKeyedSingleton<IInboxStore>(InboxStore.InMemory, (serviceProvider, _) =>
            serviceProvider.GetRequiredService<InMemoryInboxStore>());

        return services;
    }
}
