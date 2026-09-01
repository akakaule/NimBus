using NimBus.Core.Events;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;

namespace NimBus.Core.CircuitBreaker;

/// <summary>Records terminal handler outcomes without changing pipeline semantics.</summary>
public sealed class CircuitBreakerRecorderBehavior(IEndpointCircuitBreaker circuitBreaker) : IMessagePipelineBehavior
{
    private readonly IEndpointCircuitBreaker _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));

    /// <inheritdoc />
    public async Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (IsHeartbeat(context))
        {
            await next(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await next(context, cancellationToken).ConfigureAwait(false);
            _circuitBreaker.RecordSuccess();
        }
        catch (Exception exception)
        {
            _circuitBreaker.RecordFailure(exception);
            throw;
        }
    }

    private static bool IsHeartbeat(IMessageContext context)
    {
        try
        {
            return string.Equals(context.EventTypeId, Heartbeat.EventTypeId, StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidMessageException)
        {
            return false;
        }
    }
}
