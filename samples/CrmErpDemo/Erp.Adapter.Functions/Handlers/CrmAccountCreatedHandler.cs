using CrmErpDemo.Contracts.Events;
using Erp.Adapter.Functions.Clients;
using Microsoft.Extensions.Logging;
using NimBus.SDK.EventHandlers;

namespace Erp.Adapter.Functions.Handlers;

public sealed class CrmAccountCreatedHandler(
    IErpApiClient erp,
    IServiceModeClient modeClient,
    ILogger<CrmAccountCreatedHandler> logger)
    : IEventHandler<CrmAccountCreated>
{
    public async Task Handle(CrmAccountCreated message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        await ErrorModeGuard.ThrowIfEnabledAsync(modeClient, context, logger, cancellationToken);
        logger.LogInformation("Creating ERP customer from CRM account {AccountId} ({LegalName})", message.AccountId, message.LegalName);

        await erp.UpsertCustomerAsync(
            message.AccountId,
            new CustomerUpsertPayload(message.LegalName, message.TaxId, message.CountryCode),
            cancellationToken);
    }
}
