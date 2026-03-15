using NimBus.Core.Events;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.SDK;
using NimBus.SDK.EventHandlers;

namespace NimBus.EndToEnd.Tests.Infrastructure;

/// <summary>
/// Wires up the full NimBus publish-subscribe pipeline in memory.
/// Publisher → InMemoryBus → StrictMessageHandler → EventHandlerProvider → IEventHandler{T}
/// </summary>
internal sealed class EndToEndFixture
{
    private readonly InMemoryBus _publishBus;
    private readonly InMemoryBus _responseBus;
    private readonly EventHandlerProvider _eventHandlerProvider;
    private readonly IMessageHandler _messageHandler;

    public PublisherClient Publisher { get; }

    /// <summary>Messages published by the publisher.</summary>
    public InMemoryBus PublishBus => _publishBus;

    /// <summary>Response messages sent by StrictMessageHandler (resolution, error, etc.).</summary>
    public InMemoryBus ResponseBus => _responseBus;

    internal IMessageHandler MessageHandler => _messageHandler;

    public EndToEndFixture()
    {
        var loggerProvider = new TestLoggerProvider();

        _publishBus = new InMemoryBus();
        _responseBus = new InMemoryBus();

        Publisher = new PublisherClient(_publishBus, loggerProvider);

        _eventHandlerProvider = new EventHandlerProvider();
        var responseService = new ResponseService(_responseBus);

        _messageHandler = new StrictMessageHandler(
            _eventHandlerProvider,
            responseService,
            loggerProvider);
    }

    public EndToEndFixture(IRetryPolicyProvider retryPolicyProvider)
    {
        var loggerProvider = new TestLoggerProvider();

        _publishBus = new InMemoryBus();
        _responseBus = new InMemoryBus();

        Publisher = new PublisherClient(_publishBus, loggerProvider);

        _eventHandlerProvider = new EventHandlerProvider();
        var responseService = new ResponseService(_responseBus);

        _messageHandler = new StrictMessageHandler(
            _eventHandlerProvider,
            responseService,
            loggerProvider,
            retryPolicyProvider);
    }

    /// <summary>
    /// Constructor for pipeline and lifecycle integration tests.
    /// Uses a PipelineMessageHandler that wraps event handling with pipeline behaviors and lifecycle notifications.
    /// </summary>
    public EndToEndFixture(MessagePipeline? pipeline, MessageLifecycleNotifier? notifier)
    {
        var loggerProvider = new TestLoggerProvider();

        _publishBus = new InMemoryBus();
        _responseBus = new InMemoryBus();

        Publisher = new PublisherClient(_publishBus, loggerProvider);

        _eventHandlerProvider = new EventHandlerProvider();

        _messageHandler = new PipelineMessageHandler(
            loggerProvider,
            _eventHandlerProvider,
            pipeline,
            notifier);
    }

    /// <summary>
    /// Registers an event handler factory.
    /// </summary>
    public void RegisterHandler<TEvent>(Func<IEventHandler<TEvent>> handlerFactory) where TEvent : IEvent
    {
        _eventHandlerProvider.RegisterHandler(handlerFactory);
    }

    /// <summary>
    /// Delivers all pending published messages through the subscriber pipeline.
    /// </summary>
    public Task DeliverAll(CancellationToken cancellationToken = default)
    {
        return _publishBus.DeliverAll(_messageHandler, cancellationToken);
    }

    /// <summary>
    /// Delivers all pending published messages and returns per-message results.
    /// </summary>
    public Task<List<DeliveryResult>> DeliverAllWithResults(CancellationToken cancellationToken = default)
    {
        return _publishBus.DeliverAllWithResults(_messageHandler, cancellationToken);
    }
}

/// <summary>
/// A MessageHandler subclass that supports pipeline behaviors and lifecycle observers
/// while routing EventRequest messages to an EventHandlerProvider.
/// Used for E2E pipeline integration tests.
/// </summary>
internal sealed class PipelineMessageHandler : MessageHandler
{
    private readonly IEventContextHandler _eventContextHandler;

    public PipelineMessageHandler(
        Core.Logging.ILoggerProvider loggerProvider,
        IEventContextHandler eventContextHandler,
        MessagePipeline? pipeline,
        MessageLifecycleNotifier? notifier)
        : base(loggerProvider, pipeline, notifier)
    {
        _eventContextHandler = eventContextHandler;
    }

    public override async Task HandleEventRequest(
        IMessageContext messageContext,
        Core.Logging.ILogger logger,
        CancellationToken cancellationToken = default)
    {
        await _eventContextHandler.Handle(messageContext, logger, cancellationToken);
        await messageContext.Complete(cancellationToken);
    }
}
