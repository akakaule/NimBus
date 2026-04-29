using Crm.Adapter.Clients;
using CrmErpDemo.Contracts.Events;
using Microsoft.Extensions.Logging;
using NimBus.SDK.EventHandlers;

namespace Crm.Adapter.Handlers;

public sealed class ErpContactUpdatedHandler(ICrmApiClient crm, ILogger<ErpContactUpdatedHandler> logger)
    : IEventHandler<ErpContactUpdated>
{
    public async Task Handle(ErpContactUpdated message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Upserting CRM contact {ContactId} from ERP update", message.ContactId);

        // message.CustomerId is the ERP customer id; the CRM API resolves it to
        // its own Account.Id via Accounts.ErpCustomerId before storing the contact.
        await crm.UpsertContactAsync(
            message.ContactId,
            new ContactPayload(message.CustomerId, message.FirstName, message.LastName, message.Email, message.Phone),
            cancellationToken);
    }
}
