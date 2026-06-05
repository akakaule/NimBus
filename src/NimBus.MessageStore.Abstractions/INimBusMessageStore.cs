namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Convenience aggregate of every storage contract a NimBus consumer might need
/// (message tracking, subscriptions, endpoint metadata, metrics, event schema registry).
/// Implemented by each storage provider so that consumers like the management WebApp — which
/// touches all of these concerns — can take a single dependency.
///
/// Code that only needs one concern should inject the corresponding interface
/// (<see cref="IMessageTrackingStore"/>, <see cref="ISubscriptionStore"/>,
/// <see cref="IEndpointMetadataStore"/>, <see cref="IMetricsStore"/>, or
/// <see cref="IEventSchemaStore"/>) directly.
/// </summary>
public interface INimBusMessageStore
    : IMessageTrackingStore,
      ISubscriptionStore,
      IEndpointMetadataStore,
      IMetricsStore,
      IEventSchemaStore
{
}
