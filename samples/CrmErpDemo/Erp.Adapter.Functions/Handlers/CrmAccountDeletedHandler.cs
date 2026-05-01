using CrmErpDemo.Contracts.Events;
using Erp.Adapter.Functions.Clients;
using Microsoft.Extensions.Logging;
using NimBus.SDK.EventHandlers;

namespace Erp.Adapter.Functions.Handlers;

public sealed class CrmAccountDeletedHandler(
    IErpApiClient erp,
    IServiceModeClient modeClient,
    ILogger<CrmAccountDeletedHandler> logger)
    : IEventHandler<CrmAccountDeleted>
{
    public async Task Handle(CrmAccountDeleted message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        await ErrorModeGuard.ThrowIfEnabledAsync(modeClient, context, logger, cancellationToken);
        logger.LogInformation(
            "Marking ERP customer deleted (matched by CrmAccountId {AccountId})",
            message.AccountId);

        await erp.MarkCustomerByCrmIdDeletedAsync(message.AccountId, cancellationToken);
    }
}
