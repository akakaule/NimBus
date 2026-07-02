using System;
using System.Collections.Generic;
using System.Threading;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using System.Threading.Tasks;

namespace NimBus.SDK
{
    public interface IPublisherClient
    {
        Task Publish(IEvent @event);

        /// <summary>
        /// Publishes a pre-built, dynamically-typed message (no compiled IEvent). The caller sets
        /// EventTypeId + MessageContent.EventContent. Used by the agent REST API (spec 022).
        /// </summary>
        Task Publish(IMessage message, CancellationToken cancellationToken = default);

        Task Publish(IEvent @event, string sessionId, string correlationId);

        Task Publish(IEvent @event, string sessionId, string correlationId, string messageId);
        /// <summary>
        /// Pre-release - use with care!
        /// Publish multiple messages at once. Make sure batch size enforced by Azure Service Bus is taken into account.
        /// </summary>
        /// <param name="events">List of events you want to publish. Make sure to make them before publishing</param>
        /// <param name="correlationId"></param>
        /// <returns></returns>
        Task PublishBatch(IEnumerable<IEvent> events, string correlationId = null);
        /// <summary>
        /// Use to get batch of maximum possible size supported by Azure Service Bus
        /// </summary>
        /// <param name="events">Events you want to split into multiple batches</param>
        /// <returns>Batches of events</returns>
        IEnumerable<IEnumerable<IEvent>> GetBatches(List<IEvent> events);

        /// <summary>
        /// Publishes any number of events, automatically split into pages that
        /// fit the Azure Service Bus batch size. Preferred over
        /// <see cref="GetBatches"/> + <see cref="PublishBatch"/> — the
        /// <see cref="PublisherClient"/> implementation builds and serializes
        /// each event exactly once. This default implementation delegates to
        /// GetBatches + PublishBatch so existing test doubles keep working.
        /// </summary>
        /// <param name="events">Events to publish, in order.</param>
        /// <param name="correlationId">Correlation id applied to every message; a new GUID when null.</param>
        async Task PublishBatches(IEnumerable<IEvent> events, string correlationId = null)
        {
            foreach (var batch in GetBatches(new List<IEvent>(events)))
            {
                await PublishBatch(batch, correlationId);
            }
        }

        /// <summary>
        /// Sends a request and awaits a typed response with timeout.
        /// Uses Azure Service Bus sessions for reply correlation.
        /// </summary>
        Task<TResponse> Request<TRequest, TResponse>(TRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
            where TRequest : IEvent
            where TResponse : class
        {
            throw new NotSupportedException("Request/response requires a ServiceBusClient. Use PublisherClient with a ServiceBusClient constructor.");
        }
    }
}
