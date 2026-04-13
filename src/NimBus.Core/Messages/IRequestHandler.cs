using NimBus.Core.Events;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Messages;

/// <summary>
/// Handles a request message and returns a typed response.
/// Used for synchronous request/response patterns over the bus.
/// </summary>
/// <typeparam name="TRequest">The request event type.</typeparam>
/// <typeparam name="TResponse">The response type (serialized as JSON).</typeparam>
public interface IRequestHandler<TRequest, TResponse>
    where TRequest : IEvent
    where TResponse : class
{
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken = default);
}
