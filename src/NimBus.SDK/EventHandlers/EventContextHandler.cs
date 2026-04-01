using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK.EventHandlers
{
    public class EventHandlerProvider : IEventContextHandler
    {
        private readonly ConcurrentDictionary<string, Func<IEventJsonHandler>> _handlerBuilders;

        public EventHandlerProvider()
        {
            _handlerBuilders = new ConcurrentDictionary<string, Func<IEventJsonHandler>>();
        }

        /// <summary>
        /// Abstract override, handles <see cref="IEventContext"/> regardless of when/how/why the event message was sent.
        /// </summary>
        public async Task Handle(IMessageContext context, CancellationToken cancellationToken = default)
        {
            // Get handler from factory.
            var handler = GetHandler(context.MessageContent.EventContent.EventTypeId);

            // Invoke handler.
            await handler.Handle(context, cancellationToken);
        }

        public void RegisterHandler<T_Event>(Func<IEventHandler<T_Event>> eventHandlerFactory)
            where T_Event : IEvent
        {
            IEventJsonHandler buildEventJsonHandler()
            {
                // Build event handler, and adapt it to handle json.
                IEventHandler<T_Event> eventHandler = eventHandlerFactory.Invoke();
                return new EventJsonHandler<T_Event>(eventHandler);
            }

            var eventTypeId = new EventType<T_Event>().Id;
            _handlerBuilders[eventTypeId] = buildEventJsonHandler;
        }

        private IEventJsonHandler GetHandler(string eventTypeId)
        {
            if (!_handlerBuilders.TryGetValue(eventTypeId, out var factory))
                throw new EventHandlerNotFoundException($"Event handler not registered for Event type {eventTypeId}");

            // Build and return event json handler.
            return factory.Invoke();
        }
    }
}
