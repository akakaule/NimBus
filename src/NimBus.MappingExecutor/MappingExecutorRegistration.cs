using Microsoft.Extensions.DependencyInjection;
using NimBus.Core.Transform;
using NimBus.SDK.Extensions;

namespace NimBus.MappingExecutor;

/// <summary>
/// DI extension that registers the Mapping Executor and wires it as a NimBus subscriber on the
/// pre-provisioned Mapping Zone endpoint (spec 023).
/// </summary>
public static class MappingExecutorRegistration
{
    /// <summary>
    /// Registers the Mapping Executor and all its dependencies, then adds a NimBus subscriber on
    /// <paramref name="mappingZoneEndpointId"/> whose catch-all fallback handler is the executor.
    /// </summary>
    /// <remarks>
    /// Registration mirrors the Agent Zone host (<c>CrmErpDemo.AgentZone</c>):
    /// <list type="bullet">
    ///   <item><c>AddNimBusPublisher</c> on the Mapping Zone topic so transformed target events are
    ///         published back to the same topic; subscription filters (dynamic forwards) route them
    ///         onward to target consumers.</item>
    ///   <item><c>AddNimBusSubscriber</c> on the Mapping Zone endpoint with
    ///         <c>AddDynamicFallbackHandler</c> so every inbound event is dispatched to the executor
    ///         regardless of its EventTypeId — the executor consults the mapping registry per message.</item>
    ///   <item><c>AddNimBusReceiver</c> to start the session processor hosted service.</item>
    ///   <item><c>AddNimBusDeferredProcessorHostedService</c> so parked sessions are drained when
    ///         an operator resubmits or skips a parked message.</item>
    /// </list>
    /// Callers must also register an <c>INimBusMessageStore</c> (or individually
    /// <c>IEventMappingStore</c> and <c>IEventSchemaStore</c>) and a <c>ServiceBusClient</c>.
    /// </remarks>
    /// <param name="services">The DI service collection.</param>
    /// <param name="mappingZoneEndpointId">
    /// The Mapping Zone endpoint id (Service Bus topic name). Used for both the subscriber and the
    /// publisher so the executor receives from — and publishes back to — the same topic.
    /// </param>
    /// <returns>The <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddMappingExecutor(
        this IServiceCollection services,
        string mappingZoneEndpointId)
    {
        // Transform engine, park sink, target publisher, and executor handler.
        services.AddSingleton<IMappingTransformEngine, JsonataTransformEngine>();
        services.AddSingleton<IMappingParkSink, HandoffParkSink>();
        services.AddSingleton<IMappingTargetPublisher, PublisherTargetPublisher>();
        services.AddSingleton<MappingExecutorHandler>();

        // Publisher on the Mapping Zone topic. Transformed target events are published here;
        // dynamic forward subscriptions route them to the appropriate target consumers —
        // mirroring the pattern where the Agent Zone publishes to its own topic and subscription
        // filters dispatch the events to external consumers.
        services.AddNimBusPublisher(mappingZoneEndpointId);

        // Subscriber: every event arriving on the Mapping Zone is handled by the executor's
        // catch-all fallback — no event-type-specific handler registrations needed at startup;
        // the registry is consulted per message.
        services.AddNimBusSubscriber(mappingZoneEndpointId, sub =>
        {
            sub.AddDynamicFallbackHandler(sp => sp.GetRequiredService<MappingExecutorHandler>());
        });

        // Session processor that drives the subscriber client.
        services.AddNimBusReceiver(opts =>
        {
            opts.TopicName = mappingZoneEndpointId;
            opts.SubscriptionName = mappingZoneEndpointId;
        });

        // Deferred-replay hosted service — allows sessions blocked by a Pending+Handoff park to
        // drain deferred retries once an operator resubmits or skips the message (mirrors the
        // Agent Zone host's AddNimBusDeferredProcessorHostedService call).
        services.AddNimBusDeferredProcessorHostedService(mappingZoneEndpointId);

        return services;
    }
}
