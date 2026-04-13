using System;
using System.Collections.Generic;
using System.Threading;
using NimBus.Core.Events;
using System.Threading.Tasks;

namespace NimBus.SDK
{
    public interface IPublisherClient
    {
        Task Publish(IEvent @event);

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
