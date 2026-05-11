using Crm.Adapter.Clients;
using CrmErpDemo.Contracts.Events;
using Microsoft.Extensions.Logging;
using NimBus.SDK.EventHandlers;

namespace Crm.Adapter.Handlers;

public sealed class ErpCustomerUpdatedHandler(ICrmApiClient crm, ILogger<ErpCustomerUpdatedHandler> logger)
    : IEventHandler<ErpCustomerUpdated>
{
    public async Task Handle(ErpCustomerUpdated message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Applying ERP customer update {ErpCustomerId} ({CustomerNumber}) to CRM",
            message.ErpCustomerId, message.CustomerNumber);

        // Same upsert path as ErpCustomerCreated (Origin=Erp branch). Idempotent —
        // the CRM API endpoint either updates the matching Account or upserts one.
        await crm.UpsertFromErpAsync(
            message.ErpCustomerId,
            new AccountUpsertPayload(message.CrmAccountId ?? message.AccountId, message.LegalName, message.TaxId, message.CountryCode, message.CustomerNumber),
            cancellationToken);
    }
}
