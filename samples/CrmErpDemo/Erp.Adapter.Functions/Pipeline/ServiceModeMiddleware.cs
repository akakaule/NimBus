using Erp.Adapter.Functions.Clients;
using Microsoft.Extensions.Logging;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;

namespace Erp.Adapter.Functions.Pipeline;

/// <summary>
/// Demo-only switch: when ERP service mode is on, every inbound message is rejected with
/// an exception. NimBus then runs its normal failure path (retry → dead-letter → block
/// session), so this is the same shape as a real downstream outage.
/// </summary>
public sealed class ServiceModeMiddleware(IServiceModeClient client, ILogger<ServiceModeMiddleware> logger)
    : IMessagePipelineBehavior
{
    public async Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken cancellationToken = default)
    {
        if (await client.IsServiceModeEnabledAsync(cancellationToken))
        {
            logger.LogWarning(
                "Rejecting message {MessageId} ({EventTypeId}) — ERP is in service mode.",
                context.MessageId, context.EventTypeId);
            throw new ServiceModeRejectedException();
        }

        await next(context, cancellationToken);
    }
}

public sealed class ServiceModeRejectedException()
    : Exception("ERP is in service mode — incoming messages are temporarily rejected.");
