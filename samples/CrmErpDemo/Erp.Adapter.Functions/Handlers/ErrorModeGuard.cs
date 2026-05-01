using Erp.Adapter.Functions.Clients;
using Microsoft.Extensions.Logging;
using NimBus.SDK.EventHandlers;

namespace Erp.Adapter.Functions.Handlers;

public static class ErrorModeGuard
{
    public static async Task ThrowIfEnabledAsync(
        IServiceModeClient modeClient,
        IEventHandlerContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!await modeClient.IsErrorModeEnabledAsync(cancellationToken))
            return;

        logger.LogWarning(
            "Failing message {MessageId} ({EventType}) from handler — ERP error mode is enabled.",
            context.MessageId,
            context.EventType);

        throw new HandlerErrorModeException();
    }
}

public sealed class HandlerErrorModeException()
    : Exception("ERP is in error mode — handlers are configured to throw for every inbound message.");
