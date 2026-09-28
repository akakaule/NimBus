using NimBus.Core.Events;

namespace BusinessCentral.Api.Domain;

/// <summary>
/// Collects the events one BC operation raises, in the order they must be published. The caller
/// publishes them one by one inside the same outbox transaction as the data change.
/// </summary>
public sealed class EventBuffer
{
    private readonly List<IEvent> _events = [];

    public IReadOnlyList<IEvent> Events => _events;

    public void Add(IEvent @event) => _events.Add(@event);
}
