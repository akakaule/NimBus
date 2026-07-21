using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NimBus.Core.Extensions;
using NimBus.Core.Inbox;
using NimBus.Core.Messages;
using NimBus.SDK.Hosting;

namespace NimBus.SDK.Extensions;

internal static class InboxRegistration
{
    public static void AddServices(IServiceCollection services, InboxOptions? options)
    {
        if (options is null)
            return;

        services.TryAddSingleton<MessageLifecycleNotifier>();
        services.TryAddSingleton(TimeProvider.System);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(InboxRegistrationMarker)))
            return;

        services.AddSingleton<InboxRegistrationMarker>();
        services.AddSingleton<IHostedService>(serviceProvider =>
            new InboxPurgeHostedService(
                ResolveStore(serviceProvider, options),
                options,
                serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetService<ILogger<InboxPurgeHostedService>>()));
    }

    public static IEventContextHandler Decorate(
        IServiceProvider serviceProvider,
        IEventContextHandler inner,
        InboxOptions? options)
    {
        if (options is null)
            return inner;

        return new InboxMiddleware(
            inner,
            ResolveStore(serviceProvider, options),
            serviceProvider.GetRequiredService<MessageLifecycleNotifier>(),
            serviceProvider.GetService<ILogger<InboxMiddleware>>());
    }

    /// <summary>
    /// Creates the duplicate detector handed to <c>StrictMessageHandler</c> so the inbox check
    /// runs before the session-state guards, or <see langword="null"/> when no inbox is configured.
    /// </summary>
    public static InboxDuplicateDetector? CreateDuplicateDetector(
        IServiceProvider serviceProvider,
        InboxOptions? options)
    {
        if (options is null)
            return null;

        return new InboxDuplicateDetector(
            ResolveStore(serviceProvider, options),
            serviceProvider.GetRequiredService<MessageLifecycleNotifier>(),
            serviceProvider.GetService<ILogger<InboxDuplicateDetector>>());
    }

    private static IInboxStore ResolveStore(
        IServiceProvider serviceProvider,
        InboxOptions options)
    {
        var provider = options.DeduplicationStore!.Value;
        var store = serviceProvider.GetKeyedService<IInboxStore>(provider);
        if (store is not null)
            return store;

        var registration = provider switch
        {
            InboxStore.Cosmos => "services.AddNimBusCosmosInbox(...) from NimBus.MessageStore.CosmosDb",
            InboxStore.SqlServer => "services.AddNimBusSqlServerInbox(...) from NimBus.Inbox.SqlServer",
            InboxStore.InMemory => "services.AddNimBusInMemoryInbox() from NimBus.Testing",
            _ => "the matching inbox provider extension",
        };
        throw new InvalidOperationException(
            $"Inbox provider '{provider}' is selected but no keyed {nameof(IInboxStore)} is registered " +
            $"for {nameof(InboxStore)}.{provider}. Register it with {registration} before resolving " +
            "the subscriber or hosted services.");
    }

    private sealed class InboxRegistrationMarker;
}
