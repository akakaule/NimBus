using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NimBus.Core.Inbox;

namespace NimBus.Inbox.SqlServer;

/// <summary>
/// Dependency-injection registration for the SQL Server inbox store.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a SQL Server inbox using the supplied connection string.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The SQL Server connection string.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddNimBusSqlServerInbox(
        this IServiceCollection services,
        string connectionString)
        => services.AddNimBusSqlServerInbox(options => options.ConnectionString = connectionString);

    /// <summary>
    /// Registers one SQL Server inbox singleton as both the default and keyed inbox store.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Callback that configures the SQL inbox.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddNimBusSqlServerInbox(
        this IServiceCollection services,
        Action<SqlServerInboxOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SqlServerInboxOptions();
        configure(options);
        var inbox = new SqlServerInbox(options);

        services.TryAddSingleton(inbox);
        services.TryAddSingleton<IInboxStore>(serviceProvider =>
            serviceProvider.GetRequiredService<SqlServerInbox>());
        services.TryAddKeyedSingleton<IInboxStore>(InboxStore.SqlServer, (serviceProvider, _) =>
            serviceProvider.GetRequiredService<SqlServerInbox>());

        return services;
    }
}
