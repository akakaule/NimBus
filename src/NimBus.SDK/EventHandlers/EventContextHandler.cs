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
        private Func<IEventJsonHandler>? _fallbackBuilder;

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

        /// <summary>
        /// Registers a fallback handler invoked when no <c>EventTypeId</c>-specific handler is
        /// registered. Used by the Mapping Executor (spec 023) to handle every message arriving
        /// at the Mapping Zone and decide from the mapping registry per message.
        /// </summary>
        public void RegisterFallbackHandler(Func<IEventJsonHandler> fallbackFactory)
        {
            _fallbackBuilder = fallbackFactory ?? throw new ArgumentNullException(nameof(fallbackFactory));
        }

        /// <summary>
        /// Registers a handler for a <em>dynamically-typed</em> event keyed directly by its
        /// <paramref name="eventTypeId"/> string, with no compiled <see cref="IEvent"/> class.
        /// Used for agent-defined event types (e.g. <c>crm.contact.enriched.v1</c>) whose contract
        /// is a registered JSON Schema rather than code. The factory typically returns a
        /// <see cref="DelegateEventJsonHandler"/>.
        /// </summary>
        public void RegisterHandler(string eventTypeId, Func<IEventJsonHandler> eventJsonHandlerFactory)
        {
            if (string.IsNullOrWhiteSpace(eventTypeId))
                throw new ArgumentException("Event type id must not be null or empty.", nameof(eventTypeId));
            if (eventJsonHandlerFactory == null) throw new ArgumentNullException(nameof(eventJsonHandlerFactory));

            _handlerBuilders[eventTypeId] = eventJsonHandlerFactory;
        }

        private IEventJsonHandler GetHandler(string eventTypeId)
        {
            if (_handlerBuilders.TryGetValue(eventTypeId, out var factory))
                return factory.Invoke();
            if (_fallbackBuilder != null)
                return _fallbackBuilder.Invoke();
            throw new EventHandlerNotFoundException($"Event handler not registered for Event type {eventTypeId}");
        }
    }
}
