using CrmErpDemo.Contracts.Demo;
using Erp.Adapter.Functions.Clients;
using Microsoft.Extensions.Logging;
using NimBus.SDK.EventHandlers;

namespace Erp.Adapter.Functions.Handlers;

/// <summary>
/// First call inside every ERP handler. When erp-web has error mode on, throws the exception
/// that matches the selected failure reason (see <see cref="ErpFailureReasons"/>) before the
/// handler does any work, so NimBus records a realistic <c>ErrorType</c>/<c>ErrorText</c> for
/// the Integration Intelligence card to classify.
/// </summary>
public static class ErrorModeGuard
{
    public static async Task ThrowIfEnabledAsync(
        IServiceModeClient modeClient,
        IEventHandlerContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var mode = await modeClient.GetErrorModeAsync(cancellationToken);
        if (!mode.Enabled)
            return;

        var reason = ErpFailureReasons.Find(mode.Reason) ?? ErpFailureReasons.All[0];
        logger.LogWarning(
            "Failing message {MessageId} ({EventType}) from handler — ERP error mode is enabled with reason {Reason}.",
            context.MessageId,
            context.EventType,
            reason.Id);

        throw ErpFailureReasons.CreateException(reason.Id, context.EventType ?? "event");
    }
}
