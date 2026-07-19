using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Core.Diagnostics;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.OpenTelemetry;
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
        services.AddNimBusInstrumentation();

        var bus = new InMemoryMessageBus();
        services.AddSingleton(bus);
        // Wrap with the instrumenting decorator so the in-memory transport emits
        // the same publisher span as the production transports — required for the
        // cross-transport instrumentation conformance category.
        services.AddSingleton<ISender>(NimBusOpenTelemetryDecorators.InstrumentSender(bus, MessagingSystem.InMemory));

        services.TryAddSingleton<IPublisherClient>(sp => new PublisherClient(sp.GetRequiredService<ISender>()));

        var builder = new NimBusSubscriberBuilder(services);
        configureBuilder(builder);
        InboxRegistration.AddServices(services, builder.InboxConfiguration);

        services.TryAddSingleton<IMessageHandler>(sp =>
        {
            var eventHandlerProvider = new EventHandlerProvider(
                sp.GetRequiredService<IServiceScopeFactory>());
            var responseBus = new InMemoryMessageBus();
            var responseService = new ResponseService(responseBus);

            foreach (var registration in builder.HandlerRegistrations)
            {
                registration.Register(sp, eventHandlerProvider);
            }

            IEventContextHandler contextHandler = InboxRegistration.Decorate(
                sp,
                eventHandlerProvider,
                builder.InboxConfiguration);

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

#pragma warning disable CS0618
            var permanentFailureClassifier = sp.GetService<IPermanentFailureClassifier>();
            var failureDispositionClassifier = sp.GetService<IFailureDispositionClassifier>();
            return new StrictMessageHandler(
                contextHandler,
                responseService,
                logger,
                retryPolicyProvider,
                sp.GetService<MessagePipeline>(),
                sp.GetService<MessageLifecycleNotifier>(),
                permanentFailureClassifier,
                failureDispositionClassifier);
#pragma warning restore CS0618
        });

        return services;
    }
}
