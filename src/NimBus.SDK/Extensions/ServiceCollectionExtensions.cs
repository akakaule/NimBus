using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.Core.Outbox;
using NimBus.SDK.EventHandlers;
using NimBus.SDK.Hosting;
using NimBus.ServiceBus;
using NimBus.ServiceBus.Hosting;
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
        /// Requires a <see cref="ServiceBusClient"/> to be registered in the container.
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
        /// Requires a <see cref="ServiceBusClient"/> to be registered in the container.
        /// </summary>
        public static IServiceCollection AddNimBusPublisher(this IServiceCollection services, Action<NimBusPublisherOptions> configure)
        {
            var options = new NimBusPublisherOptions();
            configure(options);

            if (string.IsNullOrEmpty(options.Endpoint))
                throw new ArgumentException("Endpoint must be specified.", nameof(configure));

            services.TryAddSingleton<IPublisherClient>(sp =>
            {
                var client = sp.GetRequiredService<ServiceBusClient>();
                var outbox = sp.GetService<IOutbox>();

                var serviceBusSender = client.CreateSender(options.Endpoint);
                ISender sender;

                if (outbox != null)
                {
                    // When outbox is configured, use OutboxSender for transactional safety
                    sender = new OutboxSender(outbox);
                }
                else
                {
                    sender = new Sender(serviceBusSender);
                }

                return new PublisherClient(sender);
            });

            return services;
        }

        /// <summary>
        /// Registers a NimBus subscriber with the DI container.
        /// Requires a <see cref="ServiceBusClient"/> to be registered in the container.
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
        /// Requires a <see cref="ServiceBusClient"/> to be registered in the container.
        /// </summary>
        public static IServiceCollection AddNimBusSubscriber(this IServiceCollection services, Action<NimBusSubscriberOptions> configure, Action<NimBusSubscriberBuilder> configureBuilder)
        {
            var options = new NimBusSubscriberOptions();
            configure(options);

            if (string.IsNullOrEmpty(options.Endpoint))
                throw new ArgumentException("Endpoint must be specified.", nameof(configure));

            var builder = new NimBusSubscriberBuilder(services);
            configureBuilder(builder);

            services.TryAddSingleton<ISubscriberClient>(sp =>
            {
                var client = sp.GetRequiredService<ServiceBusClient>();
                var deferredProcessor = sp.GetService<IDeferredMessageProcessor>();

                var serviceBusSender = client.CreateSender(options.Endpoint);
                var sender = new Sender(serviceBusSender);
                var responseService = new ResponseService(sender);
                var eventHandlerProvider = new EventHandlerProvider();

                // Register all handlers via DI
                foreach (var registration in builder.HandlerRegistrations)
                {
                    registration.Register(sp, eventHandlerProvider);
                }

                // Build retry policy provider
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

                // Resolve pipeline, lifecycle notifier, and permanent failure classifier
                var pipeline = sp.GetService<MessagePipeline>();
                var lifecycleNotifier = sp.GetService<MessageLifecycleNotifier>();
                var permanentFailureClassifier = sp.GetService<IPermanentFailureClassifier>();

                var logger = sp.GetService<ILogger<StrictMessageHandler>>()
                    ?? (Microsoft.Extensions.Logging.ILogger)NullLogger.Instance;

                var sessionStateStore = sp.GetService<NimBus.MessageStore.Abstractions.ISessionStateStore>();

                // Create StrictMessageHandler with pipeline support
                IMessageHandler strictMessageHandler;
                if (pipeline != null || lifecycleNotifier != null)
                {
                    strictMessageHandler = new StrictMessageHandler(
                        eventHandlerProvider, responseService, logger,
                        retryPolicyProvider, pipeline, lifecycleNotifier, permanentFailureClassifier, sessionStateStore);
                }
                else if (retryPolicyProvider != null)
                {
                    strictMessageHandler = new StrictMessageHandler(
                        eventHandlerProvider, responseService, logger, retryPolicyProvider, sessionStateStore);
                }
                else
                {
                    strictMessageHandler = new StrictMessageHandler(
                        eventHandlerProvider, responseService, logger, sessionStateStore);
                }

                var serviceBusAdapter = new ServiceBusAdapter(strictMessageHandler, client, options.EntityPath);
                return new SubscriberClient(serviceBusAdapter, eventHandlerProvider);
            });

            return services;
        }

        /// <summary>
        /// Registers a NimBus receiver as a hosted service that listens to a Service Bus topic/subscription
        /// using a <see cref="Azure.Messaging.ServiceBus.ServiceBusSessionProcessor"/>.
        /// Requires <see cref="AddNimBusSubscriber(IServiceCollection, string, Action{NimBusSubscriberBuilder})"/> to be called first to register the message handler pipeline.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">Action to configure the receiver options.</param>
        /// <returns>The service collection for chaining.</returns>
        [Obsolete("Use NimBus.ServiceBus.Hosting.AddServiceBusReceiver. " +
                  "This SDK overload is a transport-leaking bridge kept for one major version " +
                  "while NimBus.SDK is detached from Azure.Messaging.ServiceBus.", false)]
        public static IServiceCollection AddNimBusReceiver(this IServiceCollection services, Action<NimBusReceiverOptions> configure)
        {
            // Materialise the SDK-typed options, then forward to the Service Bus
            // transport's receiver extension. The Service Bus extension resolves
            // IServiceBusAdapter from DI; AddNimBusSubscriber registers
            // ISubscriberClient (an IServiceBusAdapter), so we alias it here so
            // the new receiver can resolve it without a type-name dependency on
            // the SDK shell.
            var options = new NimBusReceiverOptions();
            configure(options);

            services.TryAddSingleton<IServiceBusAdapter>(sp => sp.GetRequiredService<ISubscriberClient>());

            return services.AddServiceBusReceiver(o =>
            {
                o.TopicName = options.TopicName;
                o.SubscriptionName = options.SubscriptionName;
                o.MaxConcurrentSessions = options.MaxConcurrentSessions;
                o.MaxAutoLockRenewalDuration = options.MaxAutoLockRenewalDuration;
            });
        }

        /// <summary>
        /// Registers the outbox background dispatcher as a hosted service.
        /// Call this after registering an IOutbox implementation.
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
                var client = sp.GetRequiredService<ServiceBusClient>();

                // The dispatcher needs a real sender (not OutboxSender) to actually send to Service Bus
                // We need to know the endpoint. For now, get it from the publisher options or require explicit config.
                // The endpoint is captured via the publisher registration.
                var sender = sp.GetService<OutboxDispatcherSender>();
                if (sender == null)
                    throw new InvalidOperationException(
                        "OutboxDispatcherSender is not registered. Register AddNimBusPublisher before AddNimBusOutboxDispatcher, " +
                        "or register OutboxDispatcherSender manually.");

                var dispatcherLogger = sp.GetService<ILogger<OutboxDispatcher>>();
                var hostedLogger = sp.GetService<ILogger<OutboxDispatcherHostedService>>();
                var dispatcher = new OutboxDispatcher(outbox, sender, dispatcherLogger);
                return new OutboxDispatcherHostedService(dispatcher, pollingInterval ?? TimeSpan.FromSeconds(1), batchSize, hostedLogger);
            });

            return services;
        }
    }
}
