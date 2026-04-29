using Crm.Adapter.Clients;
using CrmErpDemo.Contracts.Events;
using Microsoft.Extensions.Logging;
using NimBus.SDK.EventHandlers;

namespace Crm.Adapter.Handlers;

public sealed class ErpCustomerDeletedHandler(ICrmApiClient crm, ILogger<ErpCustomerDeletedHandler> logger)
    : IEventHandler<ErpCustomerDeleted>
{
    public async Task Handle(ErpCustomerDeleted message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Marking CRM account deleted (matched by ErpCustomerId {ErpCustomerId})",
            message.ErpCustomerId);

        await crm.MarkAccountByErpIdDeletedAsync(message.ErpCustomerId, cancellationToken);
    }
}
