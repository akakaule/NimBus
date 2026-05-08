using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Core.Diagnostics;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.Core.Outbox;
using NimBus.OpenTelemetry;
using NimBus.SDK.EventHandlers;
using NimBus.SDK.Hosting;
using NimBus.ServiceBus;
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

            services.AddNimBusInstrumentation();

            services.TryAddSingleton<ISender>(sp =>
            {
                var client = sp.GetRequiredService<ServiceBusClient>();
                var outbox = sp.GetService<IOutbox>();

                var serviceBusSender = client.CreateSender(options.Endpoint);
                ISender inner = outbox is not null
                    ? new OutboxSender(outbox)            // transactional outbox — write the row, dispatcher publishes later
                    : new Sender(serviceBusSender);       // direct publish

                // Decorator order outermost → inner: instrumenting → outbox → transport.
                return NimBusOpenTelemetryDecorators.InstrumentSender(inner, MessagingSystem.ServiceBus);
            });

            services.TryAddSingleton<IPublisherClient>(sp => new PublisherClient(sp.GetRequiredService<ISender>()));

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

                // Create StrictMessageHandler with pipeline support
                IMessageHandler strictMessageHandler;
                if (pipeline != null || lifecycleNotifier != null)
                {
                    strictMessageHandler = new StrictMessageHandler(
                        eventHandlerProvider, responseService, logger,
                        retryPolicyProvider, pipeline, lifecycleNotifier, permanentFailureClassifier);
                }
                else if (retryPolicyProvider != null)
                {
                    strictMessageHandler = new StrictMessageHandler(
                        eventHandlerProvider, responseService, logger, retryPolicyProvider);
                }
                else
                {
                    strictMessageHandler = new StrictMessageHandler(
                        eventHandlerProvider, responseService, logger);
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
        public static IServiceCollection AddNimBusReceiver(this IServiceCollection services, Action<NimBusReceiverOptions> configure)
        {
            var options = new NimBusReceiverOptions();
            configure(options);

            if (string.IsNullOrEmpty(options.TopicName))
                throw new ArgumentException("TopicName must be specified.", nameof(configure));
            if (string.IsNullOrEmpty(options.SubscriptionName))
                throw new ArgumentException("SubscriptionName must be specified.", nameof(configure));

            services.AddHostedService(sp =>
            {
                var client = sp.GetRequiredService<ServiceBusClient>();
                var subscriber = sp.GetRequiredService<ISubscriberClient>();
                var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<NimBusReceiverHostedService>>();

                return new NimBusReceiverHostedService(client, subscriber, options, logger);
            });

            return services;
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
