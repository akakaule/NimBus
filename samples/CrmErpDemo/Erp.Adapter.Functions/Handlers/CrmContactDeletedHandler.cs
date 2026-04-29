using CrmErpDemo.Contracts.Events;
using Erp.Adapter.Functions.Clients;
using Microsoft.Extensions.Logging;
using NimBus.SDK.EventHandlers;

namespace Erp.Adapter.Functions.Handlers;

public sealed class CrmContactDeletedHandler(IErpApiClient erp, ILogger<CrmContactDeletedHandler> logger)
    : IEventHandler<CrmContactDeleted>
{
    public async Task Handle(CrmContactDeleted message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Marking ERP contact {ContactId} deleted (CRM-originated delete)", message.ContactId);
        await erp.MarkContactDeletedAsync(message.ContactId, cancellationToken);
    }
}
