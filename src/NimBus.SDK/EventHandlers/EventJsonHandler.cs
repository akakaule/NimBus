using NimBus.Core.Events;
using NimBus.Core.Messages;
using Newtonsoft.Json;
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
            var @event = JsonConvert.DeserializeObject<T_Event>(context.MessageContent.EventContent.EventJson);
            var eventHandlercontext = new EventHandlerContext(context)
            {
                CorrelationId = context.CorrelationId,
                EventId = context.EventId,
                EventType = context.MessageContent.EventContent.EventTypeId,
                MessageId = context.MessageId,
            };
            return _eventHandler.Handle(@event, eventHandlercontext, cancellationToken);
        }
    }
}
