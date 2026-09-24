namespace NimBus.SDK.Hosting;

/// <summary>
/// Configuration for <see cref="DeferredMessageProcessorHostedService"/>.
/// Registered as a singleton by <c>AddNimBusDeferredProcessorHostedService</c>,
/// or passed directly when a host constructs the service itself.
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
public sealed record DeferredMessageProcessorHostedServiceOptions(
    string TopicName,
    string SubscriptionName,
    int MaxConcurrentCalls = 1);
