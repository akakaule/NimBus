using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.SDK.EventHandlers;
using NimBus.ServiceBus;

namespace NimBus.SDK
{
    /// <summary>
    /// Pipeline-terminus subscriber: a thin shell that routes
    /// <see cref="IMessageContext"/> through the registered
    /// <see cref="EventHandlerProvider"/>. The transport-neutral surface lives
    /// on <see cref="ISubscriberClient"/> (which inherits from
    /// <see cref="IMessageHandler"/>); the Service-Bus-typed
    /// <see cref="IServiceBusAdapter"/> overloads remain on the concrete class
    /// as [Obsolete] bridges for one major version while Azure-Functions
    /// consumers migrate to injecting <see cref="IServiceBusAdapter"/>
    /// directly.
    /// </summary>
    public class SubscriberClient : ISubscriberClient, IServiceBusAdapter
    {
        private readonly IMessageHandler _messageHandler;
        private readonly IServiceBusAdapter _serviceBusAdapter;
        private readonly EventHandlerProvider _eventHandlerProvider;

        /// <summary>
        /// Creates a new SubscriberClient. Used internally by DI registration via
        /// <see cref="Extensions.ServiceCollectionExtensions.AddNimBusSubscriber(Microsoft.Extensions.DependencyInjection.IServiceCollection, string, System.Action{Extensions.NimBusSubscriberBuilder})"/>.
        /// </summary>
        /// <param name="messageHandler">Transport-neutral pipeline terminus (the strict message handler).</param>
        /// <param name="serviceBusAdapter">Service-Bus-typed bridge that wraps <paramref name="messageHandler"/> for the [Obsolete] ASB overloads.</param>
        /// <param name="eventHandlerProvider">Registry that <see cref="RegisterHandler{T_Event}"/> writes into.</param>
        internal SubscriberClient(IMessageHandler messageHandler, IServiceBusAdapter serviceBusAdapter, EventHandlerProvider eventHandlerProvider)
        {
            _messageHandler = messageHandler ?? throw new ArgumentNullException(nameof(messageHandler));
            _serviceBusAdapter = serviceBusAdapter ?? throw new ArgumentNullException(nameof(serviceBusAdapter));
            _eventHandlerProvider = eventHandlerProvider ?? throw new ArgumentNullException(nameof(eventHandlerProvider));
        }

        public Task Handle(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            _messageHandler.Handle(messageContext, cancellationToken);

        [Obsolete("Inject IServiceBusAdapter directly instead. ASB-typed Handle overloads are kept on SubscriberClient for one major version while Azure-Functions consumers migrate.")]
        public Task Handle(ServiceBusReceivedMessage message, ServiceBusSessionMessageActions sessionActions, CancellationToken cancellationToken = default) =>
            _serviceBusAdapter.Handle(message, sessionActions, cancellationToken);

        [Obsolete("Inject IServiceBusAdapter directly instead. ASB-typed Handle overloads are kept on SubscriberClient for one major version while Azure-Functions consumers migrate.")]
        public Task Handle(ServiceBusReceivedMessage message, ServiceBusMessageActions messageActions, ServiceBusSessionMessageActions sessionActions, CancellationToken cancellationToken = default) =>
            _serviceBusAdapter.Handle(message, messageActions, sessionActions, cancellationToken);

        [Obsolete("Inject IServiceBusAdapter directly instead. ASB-typed Handle overloads are kept on SubscriberClient for one major version while Azure-Functions consumers migrate.")]
        public Task Handle(ServiceBusReceivedMessage message, ServiceBusSessionReceiver sessionReceiver, CancellationToken cancellationToken = default) =>
            _serviceBusAdapter.Handle(message, sessionReceiver, cancellationToken);

        [Obsolete("Inject IServiceBusAdapter directly instead. ASB-typed Handle overloads are kept on SubscriberClient for one major version while Azure-Functions consumers migrate.")]
        public Task Handle(ProcessSessionMessageEventArgs args, CancellationToken cancellationToken = default) =>
            _serviceBusAdapter.Handle(args, cancellationToken);

        public void RegisterHandler<T_Event>(Func<IEventHandler<T_Event>> eventHandlerFactory)
            where T_Event : IEvent
        {
            _eventHandlerProvider.RegisterHandler(eventHandlerFactory);
        }
    }
}
