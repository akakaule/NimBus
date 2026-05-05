using System;
using System.Threading;
using System.Threading.Tasks;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.SDK.EventHandlers;

namespace NimBus.SDK
{
    /// <summary>
    /// Pipeline-terminus subscriber: a thin shell that routes
    /// <see cref="IMessageContext"/> through the registered
    /// <see cref="EventHandlerProvider"/>. Transport-neutral; the Service-Bus-typed
    /// adapter is registered separately by <c>AddServiceBusReceiver</c> in
    /// <c>NimBus.ServiceBus</c>.
    /// </summary>
    public class SubscriberClient : ISubscriberClient
    {
        private readonly IMessageHandler _messageHandler;
        private readonly EventHandlerProvider _eventHandlerProvider;

        internal SubscriberClient(IMessageHandler messageHandler, EventHandlerProvider eventHandlerProvider)
        {
            _messageHandler = messageHandler ?? throw new ArgumentNullException(nameof(messageHandler));
            _eventHandlerProvider = eventHandlerProvider ?? throw new ArgumentNullException(nameof(eventHandlerProvider));
        }

        public Task Handle(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            _messageHandler.Handle(messageContext, cancellationToken);

        public void RegisterHandler<T_Event>(Func<IEventHandler<T_Event>> eventHandlerFactory)
            where T_Event : IEvent
        {
            _eventHandlerProvider.RegisterHandler(eventHandlerFactory);
        }
    }
}
