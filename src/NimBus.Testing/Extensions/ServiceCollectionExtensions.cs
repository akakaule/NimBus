using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Core.Messages;
using NimBus.SDK;
using NimBus.SDK.EventHandlers;
using NimBus.SDK.Extensions;
using System;

namespace NimBus.Testing.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNimBusTestTransport(
        this IServiceCollection services,
        Action<NimBusSubscriberBuilder> configureBuilder)
    {
        var bus = new InMemoryMessageBus();
        services.AddSingleton(bus);
        services.AddSingleton<ISender>(bus);

        services.TryAddSingleton<IPublisherClient>(sp =>
        {
            return new PublisherClient(bus);
        });

        var builder = new NimBusSubscriberBuilder(services);
        configureBuilder(builder);

        services.TryAddSingleton<IMessageHandler>(sp =>
        {
            var eventHandlerProvider = new EventHandlerProvider();
            var responseBus = new InMemoryMessageBus();
            var responseService = new ResponseService(responseBus);

            foreach (var registration in builder.HandlerRegistrations)
            {
                registration.Register(sp, eventHandlerProvider);
            }

            IRetryPolicyProvider retryPolicyProvider = null;
            if (builder.RetryPolicyConfigurator != null)
            {
                var provider = new DefaultRetryPolicyProvider();
                builder.RetryPolicyConfigurator(provider);
                retryPolicyProvider = provider;
            }
            else
            {
                retryPolicyProvider = sp.GetService<IRetryPolicyProvider>();
            }

            var logger = sp.GetService<ILogger<StrictMessageHandler>>()
                ?? (Microsoft.Extensions.Logging.ILogger)NullLogger.Instance;

            var sessionStateStore = sp.GetService<NimBus.MessageStore.Abstractions.ISessionStateStore>();

            if (retryPolicyProvider != null)
            {
                return new StrictMessageHandler(
                    eventHandlerProvider, responseService, logger, retryPolicyProvider, sessionStateStore);
            }

            return new StrictMessageHandler(
                eventHandlerProvider, responseService, logger, sessionStateStore);
        });

        return services;
    }
}
