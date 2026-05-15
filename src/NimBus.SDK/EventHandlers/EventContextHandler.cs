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
            RegisterHandler(typeof(T_Event), eventHandlerFactory);
        }

        public void RegisterHandler(Type eventType, Func<object> eventHandlerFactory)
        {
            if (eventType == null) throw new ArgumentNullException(nameof(eventType));
            if (eventHandlerFactory == null) throw new ArgumentNullException(nameof(eventHandlerFactory));
            if (!typeof(IEvent).IsAssignableFrom(eventType))
                throw new ArgumentException($"Event type '{eventType.FullName}' must implement {nameof(IEvent)}.", nameof(eventType));

            IEventJsonHandler buildEventJsonHandler()
            {
                var eventHandler = eventHandlerFactory.Invoke();
                var adapterType = typeof(EventJsonHandler<>).MakeGenericType(eventType);
                return (IEventJsonHandler)(Activator.CreateInstance(adapterType, eventHandler)
                    ?? throw new InvalidOperationException($"Could not create handler adapter for event type '{eventType.FullName}'."));
            }

            var eventTypeId = new EventType(eventType).Id;
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
