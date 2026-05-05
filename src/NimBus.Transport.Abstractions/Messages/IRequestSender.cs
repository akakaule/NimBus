using System;
using System.Threading;
using System.Threading.Tasks;
using NimBus.Core.Events;

namespace NimBus.Core.Messages
{
    /// <summary>
    /// Transport-neutral request/response surface. Sends a request to a
    /// destination keyed by event type and awaits a typed reply within the
    /// supplied timeout. The Service Bus implementation uses session-based
    /// reply queues (<c>{topic}-reply</c> with a per-request session id);
    /// other transports (RabbitMQ, in-memory) plug their own correlation
    /// strategy behind this surface.
    /// </summary>
    public interface IRequestSender
    {
        /// <summary>
        /// Sends <paramref name="request"/> and awaits a deserialized
        /// <typeparamref name="TResponse"/>. Throws <see cref="TimeoutException"/>
        /// when no reply arrives within <paramref name="timeout"/>.
        /// </summary>
        Task<TResponse> Request<TRequest, TResponse>(TRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
            where TRequest : IEvent
            where TResponse : class;
    }
}
