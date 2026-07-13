using NimBus.Core.Events;
using NimBus.Core.Messages;
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

            var eventJson = context.MessageContent?.EventContent?.EventJson
                ?? throw new JsonSerializationException(
                    $"Event payload for '{context.EventTypeId}' is missing.");
            var @event = JsonConvert.DeserializeObject<T_Event>(eventJson, Constants.SafeJsonSettings)
                ?? throw new JsonSerializationException(
                    $"Event payload for '{context.EventTypeId}' deserialized to null.");
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
