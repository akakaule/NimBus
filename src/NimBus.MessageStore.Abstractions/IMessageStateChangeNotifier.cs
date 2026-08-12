using System.Threading;
using System.Threading.Tasks;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Fires after the storage layer has persisted a message state transition. Used by
/// the management WebApp to push live updates to connected operators via SignalR.
/// Implementations may be no-op (default), in-process (the WebApp) or out-of-process
/// (e.g. a Service Bus bridge that fans state-change events from the Resolver to
/// the WebApp). The Resolver invokes this after every successful status write so
/// realtime UI updates work for any storage provider, not just Cosmos DB Change Feed.
/// </summary>
public interface IMessageStateChangeNotifier
{
    Task NotifyEndpointStateChangedAsync(string endpointId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fires after an endpoint's heartbeat state changed. Default-implemented as a no-op
    /// so notifiers that predate the platform heartbeat keep compiling.
    /// </summary>
    /// <param name="endpointId">The endpoint whose heartbeat state changed.</param>
    /// <param name="cancellationToken">Cancels the notification.</param>
    Task NotifyHeartbeatChangedAsync(string endpointId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Fires after a platform service's liveness changed. Default-implemented as a no-op
    /// so notifiers that predate the platform heartbeat keep compiling.
    /// </summary>
    /// <param name="serviceId">The platform service whose liveness changed.</param>
    /// <param name="cancellationToken">Cancels the notification.</param>
    Task NotifyServiceHealthChangedAsync(string serviceId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>
/// Default no-op notifier registered when no SignalR/Service-Bus bridge is wired up
/// (typical for Resolver hosts in environments where realtime UI updates aren't needed).
/// </summary>
public sealed class NoopMessageStateChangeNotifier : IMessageStateChangeNotifier
{
    public Task NotifyEndpointStateChangedAsync(string endpointId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
