using NimBus.Core.Events;
using NimBus.Core.Logging;
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

        public Task Handle(IMessageContext context, ILogger logger, CancellationToken cancellationToken = default)
        {
            var @event = JsonConvert.DeserializeObject<T_Event>(context.MessageContent.EventContent.EventJson);
            var eventHandlercontext = new EventHandlerContext { CorrelationId = context.CorrelationId, EventId = context.EventId, EventType = context.MessageContent.EventContent.EventTypeId };
            return _eventHandler.Handle(@event, logger, eventHandlercontext, cancellationToken);
        }
    }
}
