using CrmErpDemo.Contracts.Events;
using Erp.Adapter.Functions.Clients;
using Microsoft.Extensions.Logging;
using NimBus.SDK.EventHandlers;

namespace Erp.Adapter.Functions.Handlers;

public sealed class CrmAccountCreatedHandler(IErpApiClient erp, ILogger<CrmAccountCreatedHandler> logger)
    : IEventHandler<CrmAccountCreated>
{
    public async Task Handle(CrmAccountCreated message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Creating ERP customer from CRM account {AccountId} ({LegalName})", message.AccountId, message.LegalName);

        await erp.UpsertCustomerAsync(
            message.AccountId,
            new CustomerUpsertPayload(message.LegalName, message.TaxId, message.CountryCode),
            cancellationToken);
    }
}
