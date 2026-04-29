using CrmErpDemo.Contracts.Events;
using Erp.Adapter.Functions.Clients;
using Microsoft.Extensions.Logging;
using NimBus.SDK.EventHandlers;

namespace Erp.Adapter.Functions.Handlers;

public sealed class CrmContactCreatedHandler(IErpApiClient erp, ILogger<CrmContactCreatedHandler> logger)
    : IEventHandler<CrmContactCreated>
{
    public async Task Handle(CrmContactCreated message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Creating ERP contact {ContactId} for account {AccountId}", message.ContactId, message.AccountId);

        // message.AccountId is the CRM account id; the ERP API resolves it to its
        // own Customer.Id via Customers.CrmAccountId before storing the contact.
        await erp.UpsertContactAsync(
            message.ContactId,
            new ContactUpsertPayload(message.AccountId, message.FirstName, message.LastName, message.Email, message.Phone),
            cancellationToken);
    }
}
