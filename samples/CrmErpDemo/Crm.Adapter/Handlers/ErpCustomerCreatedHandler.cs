using Crm.Adapter.Clients;
using CrmErpDemo.Contracts.Events;
using Microsoft.Extensions.Logging;
using NimBus.SDK.EventHandlers;

namespace Crm.Adapter.Handlers;

public sealed class ErpCustomerCreatedHandler(ICrmApiClient crm, ILogger<ErpCustomerCreatedHandler> logger)
    : IEventHandler<ErpCustomerCreated>
{
    public async Task Handle(ErpCustomerCreated message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        if (message.Origin == CustomerOrigin.Crm)
        {
            logger.LogInformation(
                "Linking CRM account {AccountId} to ERP customer {ErpCustomerId} ({CustomerNumber})",
                message.AccountId, message.ErpCustomerId, message.CustomerNumber);

            await crm.LinkErpAsync(message.AccountId, message.ErpCustomerId, message.CustomerNumber, cancellationToken);
            return;
        }

        logger.LogInformation(
            "Upserting CRM account from ERP-originated customer {ErpCustomerId} ({CustomerNumber})",
            message.ErpCustomerId, message.CustomerNumber);

        await crm.UpsertFromErpAsync(
            message.ErpCustomerId,
            new AccountUpsertPayload(message.LegalName, message.TaxId, message.CountryCode, message.CustomerNumber),
            cancellationToken);
    }
}
