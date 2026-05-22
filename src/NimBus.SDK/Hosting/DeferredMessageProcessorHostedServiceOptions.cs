namespace NimBus.SDK.Hosting
{
    /// <summary>
    /// DI-resolved configuration for <see cref="DeferredMessageProcessorHostedService"/>.
    /// Registered as a singleton by <c>AddNimBusSubscriber</c> with values pulled
    /// from <see cref="Extensions.NimBusSubscriberOptions"/>.
    /// </summary>
    internal sealed record DeferredMessageProcessorHostedServiceOptions(
        string TopicName,
        string SubscriptionName);
}
