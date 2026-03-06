using NimBus.Core.Events;
using NimBus.SDK.EventHandlers;
using NimBus.ServiceBus;
using System;

namespace NimBus.SDK
{
    public interface ISubscriberClient : IServiceBusAdapter 
    {
        void RegisterHandler<T_Event>(Func<IEventHandler<T_Event>> eventHandlerFactory) where T_Event : IEvent;
    }
}
