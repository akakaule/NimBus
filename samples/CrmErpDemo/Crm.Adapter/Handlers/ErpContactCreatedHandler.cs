using Crm.Adapter.Clients;
using CrmErpDemo.Contracts.Events;
using Microsoft.Extensions.Logging;
using NimBus.SDK.EventHandlers;

namespace Crm.Adapter.Handlers;

public sealed class ErpContactCreatedHandler(ICrmApiClient crm, ILogger<ErpContactCreatedHandler> logger)
    : IEventHandler<ErpContactCreated>
{
    public async Task Handle(ErpContactCreated message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Upserting CRM contact {ContactId} from ERP-originated create",
            message.ContactId);

        // message.CustomerId is the ERP customer id; the CRM API resolves it to
        // its own Account.Id via Accounts.ErpCustomerId before storing the contact.
        await crm.UpsertContactAsync(
            message.ContactId,
            new ContactPayload(message.CustomerId, message.FirstName, message.LastName, message.Email, message.Phone),
            cancellationToken);
    }
}
