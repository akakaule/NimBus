using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.SDK.EventHandlers;
using System;

namespace NimBus.SDK
{
    /// <summary>
    /// Transport-neutral subscriber surface. Inherits from
    /// <see cref="IMessageHandler"/> so consumers handle messages via the
    /// pipeline-terminus contract; the Service-Bus-typed bridge lives inside
    /// the concrete <see cref="SubscriberClient"/> as an [Obsolete] surface
    /// kept for one major version while Azure-Functions-bound consumers
    /// migrate to <c>IServiceBusAdapter</c> directly.
    /// </summary>
    public interface ISubscriberClient : IMessageHandler
    {
        void RegisterHandler<T_Event>(Func<IEventHandler<T_Event>> eventHandlerFactory) where T_Event : IEvent;
    }
}
