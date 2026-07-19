using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Core.Diagnostics;
using NimBus.Core.Events;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.Core.Outbox;
using NimBus.OpenTelemetry;
using NimBus.SDK.EventHandlers;
using NimBus.SDK.Hosting;
using NimBus.ServiceBus;
using Microsoft.Extensions.Hosting;
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
        /// Registers a NimBus publisher and configures AsyncAPI enrichment for the events it publishes.
        /// The send wiring is identical to <see cref="AddNimBusPublisher(IServiceCollection, string)"/>;
        /// <paramref name="configure"/> only records documentation metadata (via a shared
        /// <see cref="AsyncApiEnrichmentRegistry"/> singleton) that the exporter surfaces.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="endpoint">The endpoint (topic name) to publish messages to.</param>
        /// <param name="configure">Configures per-event AsyncAPI enrichment.</param>
        /// <returns>The service collection for chaining.</returns>
        public static IServiceCollection AddNimBusPublisher(
            this IServiceCollection services, string endpoint, Action<NimBusPublisherBuilder> configure)
        {
            if (configure is null) throw new ArgumentNullException(nameof(configure));

            // Unchanged send wiring — the fluent path is metadata-only.
            services.AddNimBusPublisher(options => options.Endpoint = endpoint);

            var registry = GetOrCreateEnrichmentRegistry(services);
            configure(new NimBusPublisherBuilder(registry));
            return services;
        }

        // Get-or-create the single shared AsyncApiEnrichmentRegistry in this container, mirroring the
        // SubscriberEndpointMarker scan pattern below, so multiple publisher registrations (and the
        // AddNimBusAsyncApiDocument bridge) all accumulate onto one instance.
        internal static AsyncApiEnrichmentRegistry GetOrCreateEnrichmentRegistry(IServiceCollection services)
        {
            foreach (var descriptor in services)
            {
                if (descriptor.ServiceType == typeof(AsyncApiEnrichmentRegistry)
                    && descriptor.ImplementationInstance is AsyncApiEnrichmentRegistry existing)
                {
                    return existing;
                }
            }

            var registry = new AsyncApiEnrichmentRegistry();
            services.AddSingleton(registry);
            return registry;
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

            // Build the publisher's sender via a private factory so a pre-existing
            // ISender registration cannot shadow it. The TryAddSingleton<ISender>
            // below preserves the public ISender resolution path for callers that
            // want it, but IPublisherClient is bound to OUR sender — not to
            // whatever ambient ISender the container happens to resolve.
            ISender BuildPublisherSender(IServiceProvider sp)
            {
                var client = sp.GetRequiredService<ServiceBusClient>();
                var outbox = sp.GetService<IOutbox>();

                var serviceBusSender = client.CreateSender(options.Endpoint);
                ISender inner = outbox is not null
                    ? new OutboxSender(outbox)            // transactional outbox — write the row, dispatcher publishes later
                    : new Sender(serviceBusSender);       // direct publish

                // Decorator order outermost → inner: instrumenting → outbox → transport.
                return NimBusOpenTelemetryDecorators.InstrumentSender(inner, MessagingSystem.ServiceBus);
            }

            services.TryAddSingleton<ISender>(BuildPublisherSender);
            services.TryAddSingleton<IPublisherClient>(sp => new PublisherClient(BuildPublisherSender(sp), options.Endpoint, options.CloudEvents));

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
        /// <para>One subscriber endpoint per process — calling this twice with
        /// different endpoints throws, because <see cref="ISubscriberClient"/> is
        /// a non-keyed singleton and the second registration would silently lose
        /// its handler set. Host one endpoint per process (which matches the
        /// Aspire / Functions topology in samples/CrmErpDemo); if multi-endpoint
        /// hosting is needed, file an issue to discuss keyed-ISubscriberClient
        /// support.</para>
        /// </summary>
        public static IServiceCollection AddNimBusSubscriber(this IServiceCollection services, Action<NimBusSubscriberOptions> configure, Action<NimBusSubscriberBuilder> configureBuilder)
        {
            var options = new NimBusSubscriberOptions();
            configure(options);

            if (string.IsNullOrEmpty(options.Endpoint))
                throw new ArgumentException("Endpoint must be specified.", nameof(configure));

            // One-endpoint-per-process guard. A second call against the *same*
            // endpoint is benign (TryAdd would keep the first registration anyway);
            // a second call against a *different* endpoint silently bound the
            // second endpoint's handlers to a no-op, which is the bug.
            foreach (var descriptor in services)
            {
                if (descriptor.ServiceType == typeof(SubscriberEndpointMarker)
                    && descriptor.ImplementationInstance is SubscriberEndpointMarker existing
                    && !string.Equals(existing.Endpoint, options.Endpoint, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"AddNimBusSubscriber was already called for endpoint '{existing.Endpoint}'. " +
                        $"Registering a second subscriber endpoint ('{options.Endpoint}') in the same container " +
                        "is not supported — ISubscriberClient is a non-keyed singleton. Host one endpoint per " +
                        "process (matches the Aspire / Functions topology in samples/CrmErpDemo) or file an issue " +
                        "to discuss keyed-ISubscriberClient support.");
                }
            }
            services.AddSingleton(new SubscriberEndpointMarker(options.Endpoint));

            // Match the publisher path — consumer-side spans + meters depend on
            // the same OTel ActivitySource/Meter registrations.
            services.AddNimBusInstrumentation();

            var builder = new NimBusSubscriberBuilder(services);
            configureBuilder(builder);

            // The deferred replay path is part of the subscriber contract: any session-enabled
            // endpoint may park messages on the Deferred subscription and needs this processor
            // to drain them. Register a default so adapters don't have to repeat the wiring.
            // TryAddSingleton preserves the ability to override (e.g., a custom subscription name).
            // The trigger-side BackgroundService is *not* auto-registered — see
            // AddNimBusDeferredProcessorHostedService for the explicit opt-in.
            services.TryAddSingleton<IDeferredMessageProcessor>(sp =>
                new DeferredMessageProcessor(sp.GetRequiredService<ServiceBusClient>()));

            services.TryAddSingleton<ISubscriberClient>(sp =>
            {
                var client = sp.GetRequiredService<ServiceBusClient>();

                // Handler Reply/Send calls flow through ResponseService → this
                // sender. Decorate with InstrumentSender so those outbound
                // messages emit the same publish span as IPublisherClient sends.
                // (Outbox decoration is intentionally NOT applied here —
                // handler-side replies are correlated, not transactional with
                // a local DB write; reach for IPublisherClient when transactional
                // semantics are needed.)
                var serviceBusSender = client.CreateSender(options.Endpoint);
                ISender sender = NimBusOpenTelemetryDecorators.InstrumentSender(
                    new Sender(serviceBusSender), MessagingSystem.ServiceBus);
                var responseService = new ResponseService(sender);
                var eventHandlerProvider = new EventHandlerProvider(
                    sp.GetRequiredService<IServiceScopeFactory>());

                // Register all handlers via DI
                foreach (var registration in builder.HandlerRegistrations)
                {
                    registration.Register(sp, eventHandlerProvider);
                }

                // Opt-in CloudEvents consume: build the transport read options and, when
                // enabled, wrap the handler provider in the validating decorator so an
                // invalid or unknown-type CloudEvent dead-letters with a clear reason.
                var cloudEventReadOptions = options.CloudEvents?.ToReadOptions();
                Core.Messages.IEventContextHandler contextHandler = cloudEventReadOptions != null
                    ? new CloudEventValidatingContextHandler(eventHandlerProvider)
                    : eventHandlerProvider;

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

                // Resolve pipeline, lifecycle notifier, and failure classifiers.
                var pipeline = sp.GetService<MessagePipeline>();
                var lifecycleNotifier = sp.GetService<MessageLifecycleNotifier>();
#pragma warning disable CS0618
                var permanentFailureClassifier = sp.GetService<IPermanentFailureClassifier>();
#pragma warning restore CS0618
                var failureDispositionClassifier = sp.GetService<IFailureDispositionClassifier>();

                var logger = sp.GetService<ILogger<StrictMessageHandler>>()
                    ?? (Microsoft.Extensions.Logging.ILogger)NullLogger.Instance;

                // Build StrictMessageHandler with every resolved dependency. All of
                // retryPolicyProvider, pipeline, lifecycleNotifier and both classifiers
                // are optional/nullable and the widest
                // ctor forwards nulls to the base exactly as the narrower ctors did —
                // so a single unconditional construction is behaviourally identical to
                // the old branches, and (crucially) never drops a registered
                // classifier when no pipeline/lifecycle notifier exists.
#pragma warning disable CS0618
                IMessageHandler strictMessageHandler = new StrictMessageHandler(
                    contextHandler, responseService, logger,
                    retryPolicyProvider, pipeline, lifecycleNotifier,
                    permanentFailureClassifier, failureDispositionClassifier);
#pragma warning restore CS0618

                var serviceBusAdapter = new ServiceBusAdapter(strictMessageHandler, client, options.EntityPath, cloudEventReadOptions);
                return new SubscriberClient(serviceBusAdapter, eventHandlerProvider);
            });

            // Handler code in the subscriber process commonly settles its own
            // pending handoffs (e.g. polling a status endpoint inside a hosted
            // service that runs alongside the handler). Auto-registering the
            // handoff client here means adapter authors get IHandoffClient for
            // free with no extra DI line.
            RegisterHandoffClient(services, options.Endpoint);

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

            // NOT AddHostedService: that registers via TryAddEnumerable keyed on the
            // implementation type, so a SECOND AddNimBusReceiver call (e.g. one endpoint
            // draining an extra ingress topic such as CrmErpDemo's PartnerInbound) would
            // be silently dropped. Each call must yield its own hosted receiver.
            services.AddSingleton<IHostedService>(sp =>
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
                        "OutboxDispatcherSender is not registered. AddNimBusPublisher does NOT register it — " +
                        "register it yourself as a singleton bound to your outbound endpoint before AddNimBusOutboxDispatcher, e.g. " +
                        "services.AddSingleton(sp => new OutboxDispatcherSender(sp.GetRequiredService<ServiceBusClient>().CreateSender(\"YourEndpoint\"))).");

                var dispatcherLogger = sp.GetService<ILogger<OutboxDispatcher>>();
                var hostedLogger = sp.GetService<ILogger<OutboxDispatcherHostedService>>();
                var dispatcher = new OutboxDispatcher(outbox, sender, dispatcherLogger);
                return new OutboxDispatcherHostedService(dispatcher, pollingInterval ?? TimeSpan.FromSeconds(1), batchSize, hostedLogger);
            });

            return services;
        }

        /// <summary>
        /// Registers the Worker-side deferred-processor host as an
        /// <see cref="IHostedService"/>. The host listens on the non-session
        /// <paramref name="subscriptionName"/> subscription of
        /// <paramref name="endpoint"/>'s topic, and on each trigger message
        /// drives <see cref="IDeferredMessageProcessor"/> to drain the matching
        /// session on the <c>Deferred</c> parking subscription.
        ///
        /// <para>Call this from Worker / BackgroundService hosts that own the
        /// deferred-processor trigger themselves. Azure Functions hosts that
        /// own the trigger via a <c>[ServiceBusTrigger]</c> function class
        /// should NOT call this — their function class is the trigger; the
        /// shared body lives in
        /// <see cref="NimBus.SDK.Hosting.DeferredMessageDispatcher.ProcessAsync"/>.</para>
        ///
        /// <para>Requires <see cref="AddNimBusSubscriber(IServiceCollection, string, Action{NimBusSubscriberBuilder})"/>
        /// to have been called first so <see cref="IDeferredMessageProcessor"/>
        /// is registered. The two methods are order-independent — DI resolution
        /// happens at host start.</para>
        ///
        /// <para>Idempotent: registered via
        /// <c>TryAddEnumerable(ServiceDescriptor.Singleton&lt;IHostedService, DeferredMessageProcessorHostedService&gt;())</c>,
        /// so repeated calls don't stack duplicate BackgroundServices.</para>
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="endpoint">The endpoint (topic name) whose deferred parking subscription is drained when the trigger fires.</param>
        /// <param name="subscriptionName">Name of the non-session trigger subscription. Default <c>"deferredprocessor"</c>.</param>
        /// <param name="maxConcurrentCalls">
        /// Concurrent trigger deliveries the processor handles. Default 1.
        /// <b>WARNING:</b> the trigger subscription is non-session, so 1 is the
        /// only ordering mechanism — raise this only when deferred triggers may
        /// replay out of order for this endpoint.
        /// </param>
        /// <returns>The service collection for chaining.</returns>
        public static IServiceCollection AddNimBusDeferredProcessorHostedService(
            this IServiceCollection services,
            string endpoint,
            string subscriptionName = "deferredprocessor",
            int maxConcurrentCalls = 1)
        {
            if (string.IsNullOrEmpty(endpoint))
                throw new ArgumentException("Endpoint must be specified.", nameof(endpoint));
            if (string.IsNullOrEmpty(subscriptionName))
                throw new ArgumentException("Subscription name must be specified.", nameof(subscriptionName));
            if (maxConcurrentCalls < 1)
                throw new ArgumentOutOfRangeException(nameof(maxConcurrentCalls), maxConcurrentCalls, "MaxConcurrentCalls must be at least 1.");

            services.TryAddSingleton(new DeferredMessageProcessorHostedServiceOptions(
                TopicName: endpoint,
                SubscriptionName: subscriptionName,
                MaxConcurrentCalls: maxConcurrentCalls));
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DeferredMessageProcessorHostedService>());
            return services;
        }

        /// <summary>
        /// Registers an <see cref="IHandoffClient"/> bound to <paramref name="endpoint"/>
        /// for processes that settle pending handoffs without hosting a subscriber
        /// themselves (e.g. the CrmErpDemo's <c>Erp.Api</c> background worker).
        /// Requires a <see cref="ServiceBusClient"/> to already be registered.
        ///
        /// <para>Safe to call multiple times with different endpoints — each call
        /// adds a <em>keyed</em> registration accessible via
        /// <c>IServiceProvider.GetRequiredKeyedService&lt;IHandoffClient&gt;(endpoint)</c>.
        /// The non-keyed <see cref="IHandoffClient"/> singleton resolves to the
        /// <em>first</em> registered endpoint; multi-endpoint processes should
        /// use the keyed lookup to avoid routing a settlement message to the
        /// wrong topic.</para>
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="endpoint">The subscriber endpoint whose pending-handoff rows this client settles.</param>
        /// <returns>The service collection for chaining.</returns>
        public static IServiceCollection AddNimBusHandoffClient(this IServiceCollection services, string endpoint)
        {
            if (string.IsNullOrEmpty(endpoint))
                throw new ArgumentException("Endpoint must be specified.", nameof(endpoint));

            RegisterHandoffClient(services, endpoint);
            return services;
        }

        // Shared registration helper called by both AddNimBusHandoffClient (the
        // explicit settle-only entry point) and AddNimBusSubscriber (the implicit
        // "subscriber process can settle its own handoffs" path).
        //
        // Each endpoint registers a keyed IHandoffClient so multi-endpoint
        // processes (a single host that subscribes to or settles handoffs for
        // multiple topics) can coexist without silently routing one endpoint's
        // settlement messages to another. Adapter code in those processes
        // resolves via `sp.GetRequiredKeyedService<IHandoffClient>(endpoint)`.
        //
        // For the common single-endpoint case, the first registration also
        // wires a non-keyed IHandoffClient that just forwards to its keyed
        // sibling — so handler code can keep injecting plain `IHandoffClient`
        // without ceremony. Subsequent calls to RegisterHandoffClient leave the
        // non-keyed binding pointing at the first endpoint; multi-endpoint
        // processes are expected to use the keyed lookup.
        //
        // The handoff client publishes to the subscriber endpoint's topic, so it
        // builds its own ISender — independent of any publisher-side ISender
        // registered for an outbound endpoint. The sender is wrapped with the
        // OpenTelemetry decorator so settlement control messages emit the same
        // publish spans as any other NimBus send.
        private static void RegisterHandoffClient(IServiceCollection services, string endpoint)
        {
            // Keyed registration — one slot per endpoint. AddKeyedSingleton is
            // additive (unlike TryAddKeyedSingleton, which we don't want here):
            // re-registering the same endpoint is benign; different endpoints
            // each get their own binding.
            services.AddKeyedSingleton<IHandoffClient>(endpoint, (sp, _) =>
            {
                var client = sp.GetRequiredService<ServiceBusClient>();
                var serviceBusSender = client.CreateSender(endpoint);
                ISender innerSender = NimBusOpenTelemetryDecorators.InstrumentSender(
                    new Sender(serviceBusSender), MessagingSystem.ServiceBus);
                return new HandoffClient(
                    innerSender,
                    new HandoffClientOptions { Endpoint = endpoint },
                    sp.GetService<ILogger<HandoffClient>>());
            });

            // Single-endpoint convenience binding — TryAddSingleton means the
            // first registered endpoint wins. Multi-endpoint processes must use
            // the keyed lookup; the singular IHandoffClient remains useful for
            // the (much more common) one-endpoint-per-process case.
            services.TryAddSingleton<IHandoffClient>(sp =>
                sp.GetRequiredKeyedService<IHandoffClient>(endpoint));
        }

        // Marker singleton recorded by AddNimBusSubscriber so a second call
        // against a different endpoint can fail with a clear error instead of
        // silently keeping the first registration's handlers.
        private sealed class SubscriberEndpointMarker
        {
            public SubscriberEndpointMarker(string endpoint) => Endpoint = endpoint;
            public string Endpoint { get; }
        }
    }
}
