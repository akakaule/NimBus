using NimBus.Core.Events;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK.EventHandlers
{
    /// <summary>
    /// Defines a message handler.
    /// </summary>
    /// <typeparam name="T">The type of message to be handled.</typeparam>
    public interface IEventHandler<T> where T : IEvent
    {
        /// <summary>
        /// Handles a message.
        /// </summary>
        /// <param name="message">The message to handle.</param>
        /// <param name="context">The context of the currently handled message.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task Handle(T message, IEventHandlerContext context, CancellationToken cancellationToken = default);
    }
}
