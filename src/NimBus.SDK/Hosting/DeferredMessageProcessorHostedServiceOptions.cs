namespace NimBus.SDK.Hosting
{
    /// <summary>
    /// DI-resolved configuration for <see cref="DeferredMessageProcessorHostedService"/>.
    /// Registered as a singleton by <c>AddNimBusSubscriber</c> with values pulled
    /// from <see cref="Extensions.NimBusSubscriberOptions"/>.
    /// </summary>
    /// <param name="TopicName">Endpoint (topic) whose deferred subscription is drained.</param>
    /// <param name="SubscriptionName">Name of the non-session trigger subscription.</param>
    /// <param name="MaxConcurrentCalls">
    /// Concurrent trigger deliveries the processor handles. Default 1.
    /// <b>WARNING:</b> the deferred trigger subscription is non-session —
    /// <c>MaxConcurrentCalls = 1</c> is its ONLY ordering mechanism. Raise this
    /// only when the endpoint tolerates deferred triggers replaying out of
    /// order (e.g. session-independent workloads).
    /// </param>
    internal sealed record DeferredMessageProcessorHostedServiceOptions(
        string TopicName,
        string SubscriptionName,
        int MaxConcurrentCalls = 1);
}
