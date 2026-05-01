using CrmErpDemo.Contracts.Events;
using Erp.Adapter.Functions.Clients;
using Microsoft.Extensions.Logging;
using NimBus.SDK.EventHandlers;

namespace Erp.Adapter.Functions.Handlers;

public sealed class CrmContactUpdatedHandler(
    IErpApiClient erp,
    IServiceModeClient modeClient,
    ILogger<CrmContactUpdatedHandler> logger)
    : IEventHandler<CrmContactUpdated>
{
    public async Task Handle(CrmContactUpdated message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        await ErrorModeGuard.ThrowIfEnabledAsync(modeClient, context, logger, cancellationToken);
        logger.LogInformation("Upserting ERP contact {ContactId} from CRM update", message.ContactId);

        await erp.UpsertContactAsync(
            message.ContactId,
            new ContactUpsertPayload(message.AccountId, message.FirstName, message.LastName, message.Email, message.Phone),
            cancellationToken);
    }
}
