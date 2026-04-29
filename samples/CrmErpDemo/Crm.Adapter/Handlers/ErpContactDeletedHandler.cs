using Crm.Adapter.Clients;
using CrmErpDemo.Contracts.Events;
using Microsoft.Extensions.Logging;
using NimBus.SDK.EventHandlers;

namespace Crm.Adapter.Handlers;

public sealed class ErpContactDeletedHandler(ICrmApiClient crm, ILogger<ErpContactDeletedHandler> logger)
    : IEventHandler<ErpContactDeleted>
{
    public async Task Handle(ErpContactDeleted message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Marking CRM contact {ContactId} deleted (ERP-originated delete)", message.ContactId);
        await crm.MarkContactDeletedAsync(message.ContactId, cancellationToken);
    }
}
