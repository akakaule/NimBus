using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using Newtonsoft.Json;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK.EventHandlers
{

    public class EventJsonHandler<T_Event> : IEventJsonHandler
        where T_Event : IEvent
    {
        public EventJsonHandler(IEventHandler<T_Event> eventHandler)
        {
            _eventHandler = eventHandler;
        }

        private readonly IEventHandler<T_Event> _eventHandler;

        public Task Handle(IMessageContext context, CancellationToken cancellationToken = default)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            T_Event @event;
            try
            {
                var eventJson = context.MessageContent?.EventContent?.EventJson
                    ?? throw new JsonSerializationException(
                        $"Event payload for '{context.EventTypeId}' is missing.");
                @event = JsonConvert.DeserializeObject<T_Event>(
                        eventJson,
                        Constants.CreateSafeJsonSettings())
                    ?? throw new JsonSerializationException(
                        $"Event payload for '{context.EventTypeId}' deserialized to null.");
            }
            catch (JsonException exception)
            {
                // Invalid wire payloads cannot become valid through retry. Normalize
                // every Newtonsoft parse/serialization failure to the lifecycle's
                // provider-independent permanent-failure signal.
                throw new PermanentFailureException(exception);
            }

            var eventHandlercontext = new EventHandlerContext(context)
            {
                CorrelationId = context.CorrelationId,
                EventId = context.EventId,
                EventType = context.EventTypeId,
                MessageId = context.MessageId,
            };
            return _eventHandler.Handle(@event, eventHandlercontext, cancellationToken);
        }
    }
}
