using System.Threading;
using System.Threading.Tasks;
using NimBus.MessageStore.States;

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

    Task NotifyHeartbeatChangedAsync(string endpointId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default no-op notifier registered when no SignalR/Service-Bus bridge is wired up
/// (typical for Resolver hosts in environments where realtime UI updates aren't needed).
/// </summary>
public sealed class NoopMessageStateChangeNotifier : IMessageStateChangeNotifier
{
    public Task NotifyEndpointStateChangedAsync(string endpointId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task NotifyHeartbeatChangedAsync(string endpointId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
