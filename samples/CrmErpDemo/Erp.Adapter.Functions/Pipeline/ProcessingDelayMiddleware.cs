using Erp.Adapter.Functions.Clients;
using Microsoft.Extensions.Logging;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;

namespace Erp.Adapter.Functions.Pipeline;

/// <summary>
/// Demo-only knob: when processing-delay mode is on, hold each inbound message for the
/// configured time before its handler runs — simulating a slow ERP consumer so message
/// processing visibly takes time on the Flow monitor. The live setting is read from
/// Erp.Api per message (same pull pattern as <see cref="ServiceModeMiddleware"/>).
/// </summary>
public sealed class ProcessingDelayMiddleware(IProcessingDelayClient client, ILogger<ProcessingDelayMiddleware> logger)
    : IMessagePipelineBehavior
{
    public async Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken cancellationToken = default)
    {
        var delayMs = await client.GetProcessingDelayMsAsync(cancellationToken);
        if (delayMs > 0)
        {
            logger.LogInformation(
                "Delaying message {MessageId} ({EventTypeId}) by {DelayMs}ms — ERP processing-delay mode.",
                context.MessageId, context.EventTypeId, delayMs);
            await Task.Delay(delayMs, cancellationToken);
        }

        await next(context, cancellationToken);
    }
}
