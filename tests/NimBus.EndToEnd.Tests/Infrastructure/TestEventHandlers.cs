using NimBus.Core.Events;
using NimBus.Core.Logging;
using NimBus.Core.Messages.Exceptions;
using NimBus.SDK.EventHandlers;

namespace NimBus.EndToEnd.Tests.Infrastructure;

/// <summary>
/// Recording handler for OrderPlaced events.
/// </summary>
internal sealed class RecordingOrderPlacedHandler : IEventHandler<OrderPlaced>
{
    public List<OrderPlaced> ReceivedEvents { get; } = new();
    public List<IEventHandlerContext> ReceivedContexts { get; } = new();
    public Exception? ExceptionToThrow { get; set; }
    public Func<OrderPlaced, Exception?>? ExceptionFactory { get; set; }

    public Task Handle(OrderPlaced message, ILogger logger, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        var exception = ExceptionFactory?.Invoke(message) ?? ExceptionToThrow;
        if (exception != null)
            throw exception;

        ReceivedEvents.Add(message);
        ReceivedContexts.Add(context);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Recording handler for OrderCancelled events.
/// </summary>
internal sealed class RecordingOrderCancelledHandler : IEventHandler<OrderCancelled>
{
    public List<OrderCancelled> ReceivedEvents { get; } = new();
    public List<IEventHandlerContext> ReceivedContexts { get; } = new();

    public Task Handle(OrderCancelled message, ILogger logger, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        ReceivedEvents.Add(message);
        ReceivedContexts.Add(context);
        return Task.CompletedTask;
    }
}
