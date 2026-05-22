using NimBus.Core.Events;
using NimBus.SDK;

namespace Crm.Api;

internal sealed class NoopPublisherClient : IPublisherClient
{
    public Task Publish(IEvent @event) => Task.CompletedTask;

    public Task Publish(IEvent @event, string sessionId, string correlationId) => Task.CompletedTask;

    public Task Publish(IEvent @event, string sessionId, string correlationId, string messageId) => Task.CompletedTask;

    public Task PublishBatch(IEnumerable<IEvent> events, string correlationId = "") => Task.CompletedTask;

    public IEnumerable<IEnumerable<IEvent>> GetBatches(List<IEvent> events)
    {
        yield return events;
    }
}
