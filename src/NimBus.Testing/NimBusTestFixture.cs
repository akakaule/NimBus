using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.SDK;
using NimBus.SDK.EventHandlers;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Testing;

public class NimBusTestFixture
{
    private readonly InMemoryMessageBus _publishBus;
    private readonly InMemoryMessageBus _responseBus;
    private readonly EventHandlerProvider _eventHandlerProvider;
    private readonly IMessageHandler _messageHandler;

    public PublisherClient Publisher { get; }
    public InMemoryMessageBus PublishBus => _publishBus;
    public InMemoryMessageBus ResponseBus => _responseBus;

    public NimBusTestFixture()
    {
        _publishBus = new InMemoryMessageBus();
        _responseBus = new InMemoryMessageBus();

        Publisher = new PublisherClient(_publishBus);

        _eventHandlerProvider = new EventHandlerProvider();
        var responseService = new ResponseService(_responseBus);

        _messageHandler = new StrictMessageHandler(
            _eventHandlerProvider,
            responseService,
            NullLogger.Instance,
            new InMemorySessionStateStore());
    }

    public NimBusTestFixture(IRetryPolicyProvider retryPolicyProvider)
    {
        _publishBus = new InMemoryMessageBus();
        _responseBus = new InMemoryMessageBus();

        Publisher = new PublisherClient(_publishBus);

        _eventHandlerProvider = new EventHandlerProvider();
        var responseService = new ResponseService(_responseBus);

        _messageHandler = new StrictMessageHandler(
            _eventHandlerProvider,
            responseService,
            NullLogger.Instance,
            retryPolicyProvider,
            new InMemorySessionStateStore());
    }

    public void RegisterHandler<TEvent>(Func<IEventHandler<TEvent>> handlerFactory) where TEvent : IEvent
    {
        _eventHandlerProvider.RegisterHandler(handlerFactory);
    }

    public Task DeliverAll(CancellationToken cancellationToken = default)
    {
        return _publishBus.DeliverAll(_messageHandler, cancellationToken);
    }

    public Task<List<InMemoryDeliveryResult>> DeliverAllWithResults(CancellationToken cancellationToken = default)
    {
        return _publishBus.DeliverAllWithResults(_messageHandler, cancellationToken);
    }
}
