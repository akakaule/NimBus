using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.Core.Outbox;
using NimBus.SDK.EventHandlers;
using NimBus.SDK.Hosting;
using System;

namespace NimBus.SDK.Extensions
{
    /// <summary>
    /// Extension methods for registering NimBus publisher and subscriber with Microsoft.Extensions.DependencyInjection.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Registers a NimBus publisher with the DI container.
        /// Requires a transport provider (e.g. <c>AddServiceBusTransport</c>) to be registered.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="endpoint">The endpoint (topic name) to publish messages to.</param>
        /// <returns>The service collection for chaining.</returns>
        public static IServiceCollection AddNimBusPublisher(this IServiceCollection services, string endpoint)
        {
            return services.AddNimBusPublisher(options => options.Endpoint = endpoint);
        }

        /// <summary>
        /// Registers a NimBus publisher with the DI container.
        /// Requires a transport provider (e.g. <c>AddServiceBusTransport</c>) to be registered.
        /// </summary>
        public static IServiceCollection AddNimBusPublisher(this IServiceCollection services, Action<NimBusPublisherOptions> configure)
        {
            var options = new NimBusPublisherOptions();
            configure(options);

            if (string.IsNullOrEmpty(options.Endpoint))
                throw new ArgumentException("Endpoint must be specified.", nameof(configure));

            services.TryAddSingleton<IPublisherClient>(sp =>
            {
                var outbox = sp.GetService<IOutbox>();

                ISender sender;
                if (outbox != null)
                {
                    // When outbox is configured, use OutboxSender for transactional safety
                    sender = new OutboxSender(outbox);
                }
                else
                {
                    var senderFactory = sp.GetRequiredService<Func<string, ISender>>();
                    sender = senderFactory(options.Endpoint);
                }

                var requestSender = sp.GetService<IRequestSender>();
                return new PublisherClient(sender, requestSender);
            });

            return services;
        }

        /// <summary>
        /// Registers a NimBus subscriber with the DI container.
        /// Requires a transport provider (e.g. <c>AddServiceBusTransport</c>) to be registered.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="endpoint">The endpoint (topic name) for sending responses.</param>
        /// <param name="configureBuilder">Action to configure handlers and retry policies.</param>
        /// <returns>The service collection for chaining.</returns>
        public static IServiceCollection AddNimBusSubscriber(this IServiceCollection services, string endpoint, Action<NimBusSubscriberBuilder> configureBuilder)
        {
            return services.AddNimBusSubscriber(options => options.Endpoint = endpoint, configureBuilder);
        }

        /// <summary>
        /// Registers a NimBus subscriber with the DI container.
        /// Requires a transport provider (e.g. <c>AddServiceBusTransport</c>) to be registered.
        /// </summary>
        public static IServiceCollection AddNimBusSubscriber(this IServiceCollection services, Action<NimBusSubscriberOptions> configure, Action<NimBusSubscriberBuilder> configureBuilder)
        {
            var options = new NimBusSubscriberOptions();
            configure(options);

            if (string.IsNullOrEmpty(options.Endpoint))
                throw new ArgumentException("Endpoint must be specified.", nameof(configure));

            var builder = new NimBusSubscriberBuilder(services);
            configureBuilder(builder);

            // Register the SDK-side EventHandlerProvider + populated handlers +
            // the resulting StrictMessageHandler (IMessageHandler) at DI level.
            // The transport-specific receiver (e.g. AddServiceBusReceiver in
            // NimBus.ServiceBus) is responsible for wiring its own dispatch
            // adapter on top of IMessageHandler.
            services.TryAddSingleton<EventHandlerProvider>(sp =>
            {
                var provider = new EventHandlerProvider();
                foreach (var registration in builder.HandlerRegistrations)
                {
                    registration.Register(sp, provider);
                }
                return provider;
            });

            services.TryAddSingleton<IMessageHandler>(sp =>
            {
                var senderFactory = sp.GetRequiredService<Func<string, ISender>>();
                var sender = senderFactory(options.Endpoint);
                var responseService = new ResponseService(sender);
                var eventHandlerProvider = sp.GetRequiredService<EventHandlerProvider>();

                IRetryPolicyProvider? retryPolicyProvider = null;
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

                var pipeline = sp.GetService<MessagePipeline>();
                var lifecycleNotifier = sp.GetService<MessageLifecycleNotifier>();
                var permanentFailureClassifier = sp.GetService<IPermanentFailureClassifier>();

                var logger = sp.GetService<ILogger<StrictMessageHandler>>()
                    ?? (Microsoft.Extensions.Logging.ILogger)NullLogger.Instance;

                var sessionStateStore = sp.GetService<NimBus.MessageStore.Abstractions.ISessionStateStore>();

                if (pipeline != null || lifecycleNotifier != null)
                {
                    return new StrictMessageHandler(
                        eventHandlerProvider, responseService, logger,
                        retryPolicyProvider, pipeline, lifecycleNotifier, permanentFailureClassifier, sessionStateStore);
                }
                if (retryPolicyProvider != null)
                {
                    return new StrictMessageHandler(
                        eventHandlerProvider, responseService, logger, retryPolicyProvider, sessionStateStore);
                }
                return new StrictMessageHandler(
                    eventHandlerProvider, responseService, logger, sessionStateStore);
            });

            services.TryAddSingleton<ISubscriberClient>(sp => new SubscriberClient(
                sp.GetRequiredService<IMessageHandler>(),
                sp.GetRequiredService<EventHandlerProvider>()));

            return services;
        }

        /// <summary>
        /// Registers the outbox background dispatcher as a hosted service.
        /// Call this after registering an IOutbox implementation and a transport-specific
        /// <see cref="INimBusDispatcherSender"/> (e.g. <c>OutboxDispatcherSender</c> in
        /// <c>NimBus.ServiceBus.Hosting</c>).
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="pollingInterval">How often to poll for pending messages. Default: 1 second.</param>
        /// <param name="batchSize">Maximum messages to dispatch per poll. Default: 100.</param>
        /// <returns>The service collection for chaining.</returns>
        public static IServiceCollection AddNimBusOutboxDispatcher(this IServiceCollection services, TimeSpan? pollingInterval = null, int batchSize = 100)
        {
            services.AddHostedService(sp =>
            {
                var outbox = sp.GetRequiredService<IOutbox>();

                var sender = sp.GetService<INimBusDispatcherSender>();
                if (sender == null)
                    throw new InvalidOperationException(
                        "INimBusDispatcherSender is not registered. Register a transport-specific dispatcher sender " +
                        "(e.g. NimBus.ServiceBus.Hosting.OutboxDispatcherSender) before calling AddNimBusOutboxDispatcher.");

                var dispatcherLogger = sp.GetService<ILogger<OutboxDispatcher>>();
                var hostedLogger = sp.GetService<ILogger<OutboxDispatcherHostedService>>();
                var dispatcher = new OutboxDispatcher(outbox, sender, dispatcherLogger);
                return new OutboxDispatcherHostedService(dispatcher, pollingInterval ?? TimeSpan.FromSeconds(1), batchSize, hostedLogger);
            });

            return services;
        }
    }
}
