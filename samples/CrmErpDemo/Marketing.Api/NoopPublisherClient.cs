using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.SDK;

namespace Marketing.Api;

internal sealed class NoopPublisherClient : IPublisherClient
{
    public Task Publish(IEvent @event) => Task.CompletedTask;

    public Task Publish(IMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task Publish(IEvent @event, string sessionId, string correlationId) => Task.CompletedTask;

    public Task Publish(IEvent @event, string sessionId, string correlationId, string messageId) => Task.CompletedTask;

    public Task PublishBatch(IEnumerable<IEvent> events, string correlationId = "") => Task.CompletedTask;

    public IEnumerable<IEnumerable<IEvent>> GetBatches(List<IEvent> events)
    {
        yield return events;
    }
}
