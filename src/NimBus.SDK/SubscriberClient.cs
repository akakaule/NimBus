using Azure.Messaging.ServiceBus;
using NimBus.Core.Events;
using NimBus.Core.Logging;
using NimBus.Core.Messages;
using NimBus.SDK.EventHandlers;
using NimBus.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK
{
    public class SubscriberClient : ISubscriberClient
    {
        private readonly IServiceBusAdapter _serviceBusAdapter;
        private readonly EventHandlerProvider _eventHandlerProvider;

        /// <summary>
        /// Private constructor - use CreateAsync instead for async initialization.
        /// </summary>
        private SubscriberClient(IServiceBusAdapter serviceBusAdapter, EventHandlerProvider eventHandlerProvider)
        {
            _serviceBusAdapter = serviceBusAdapter ?? throw new ArgumentNullException(nameof(serviceBusAdapter));
            _eventHandlerProvider = eventHandlerProvider ?? throw new ArgumentNullException(nameof(eventHandlerProvider));
        }

        /// <summary>
        /// Creates a new SubscriberClient asynchronously.
        /// </summary>
        /// <param name="client">The ServiceBusClient to use for sending responses.</param>
        /// <param name="endpoint">The endpoint (topic name) to send responses to.</param>
        /// <param name="loggerProvider">The logger provider.</param>
        /// <param name="deferredMessageProcessor">Optional processor for handling deferred messages from the non-session subscription.</param>
        /// <param name="entityPath">Optional entity path (queue name or topic/subscription) for receiving deferred messages.
        /// Required if using ReceiveDeferredMessageAsync in isolated worker model.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A new SubscriberClient instance.</returns>
        public static Task<SubscriberClient> CreateAsync(
            ServiceBusClient client,
            string endpoint,
            ILoggerProvider loggerProvider,
            IDeferredMessageProcessor deferredMessageProcessor = null,
            string entityPath = null,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (string.IsNullOrEmpty(endpoint)) throw new ArgumentException("Endpoint cannot be null or empty.", nameof(endpoint));

            var serviceBusSender = client.CreateSender(endpoint);
            var sender = new Sender(serviceBusSender);
            var responseService = new ResponseService(sender);
            var eventHandlerProvider = new EventHandlerProvider();

            IMessageHandler strictMessageHandler = deferredMessageProcessor != null
                ? new StrictMessageHandler(eventHandlerProvider, responseService, loggerProvider, deferredMessageProcessor, endpoint)
                : new StrictMessageHandler(eventHandlerProvider, responseService, loggerProvider);

            var serviceBusAdapter = new ServiceBusAdapter(strictMessageHandler, client, entityPath);

            return Task.FromResult(new SubscriberClient(serviceBusAdapter, eventHandlerProvider));
        }

        /// <summary>
        /// Creates a new SubscriberClient.
        /// </summary>
        /// <param name="client">The ServiceBusClient to use for sending responses.</param>
        /// <param name="endpoint">The endpoint to send responses to.</param>
        /// <param name="loggerProvider">The logger provider.</param>
        /// <param name="entityPath">Optional entity path (queue name or topic/subscription) for receiving deferred messages.
        /// Required if using ReceiveDeferredMessageAsync in isolated worker model.</param>
        [Obsolete("Use CreateAsync instead for async initialization.")]
        public SubscriberClient(ServiceBusClient client, string endpoint, ILoggerProvider loggerProvider, string entityPath = null)
            : this(client, endpoint, loggerProvider, deferredMessageProcessor: null, entityPath)
        {
        }

        /// <summary>
        /// Creates a new SubscriberClient with deferred message processing support.
        /// </summary>
        /// <param name="client">The ServiceBusClient to use for sending responses.</param>
        /// <param name="endpoint">The endpoint (topic name) to send responses to.</param>
        /// <param name="loggerProvider">The logger provider.</param>
        /// <param name="deferredMessageProcessor">The processor for handling deferred messages from the non-session subscription.</param>
        /// <param name="entityPath">Optional entity path (queue name or topic/subscription) for receiving deferred messages.
        /// Required if using ReceiveDeferredMessageAsync in isolated worker model.</param>
        [Obsolete("Use CreateAsync instead for async initialization.")]
        public SubscriberClient(ServiceBusClient client, string endpoint, ILoggerProvider loggerProvider, IDeferredMessageProcessor deferredMessageProcessor, string entityPath = null)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (string.IsNullOrEmpty(endpoint)) throw new ArgumentException("Endpoint cannot be null or empty.", nameof(endpoint));

            var serviceBusSender = client.CreateSender(endpoint);

            ISender sender = new Sender(serviceBusSender);
            IResponseService responseService = new ResponseService(sender);

            _eventHandlerProvider = new EventHandlerProvider();

            IMessageHandler strictMessageHandler = deferredMessageProcessor != null
                ? new StrictMessageHandler(_eventHandlerProvider, responseService, loggerProvider, deferredMessageProcessor, endpoint)
                : new StrictMessageHandler(_eventHandlerProvider, responseService, loggerProvider);

            _serviceBusAdapter = new ServiceBusAdapter(strictMessageHandler, client, entityPath);
        }

        public Task Handle(ServiceBusReceivedMessage message, ServiceBusSessionMessageActions sessionActions, CancellationToken cancellationToken = default) =>
            _serviceBusAdapter.Handle(message, sessionActions, cancellationToken);

        public Task Handle(ServiceBusReceivedMessage message, ServiceBusMessageActions messageActions, ServiceBusSessionMessageActions sessionActions, CancellationToken cancellationToken = default) =>
            _serviceBusAdapter.Handle(message, messageActions, sessionActions, cancellationToken);

        public Task Handle(ServiceBusReceivedMessage message, ServiceBusSessionReceiver sessionReceiver, CancellationToken cancellationToken = default) =>
            _serviceBusAdapter.Handle(message, sessionReceiver, cancellationToken);

        public void RegisterHandler<T_Event>(Func<IEventHandler<T_Event>> eventHandlerFactory)
            where T_Event : IEvent
        {
            _eventHandlerProvider.RegisterHandler(eventHandlerFactory);
        }
    }
}
