using D365Sales.Adapter.Clients;
using DynamicsBcDemo.Contracts.BusinessCentral;
using NimBus.SDK.EventHandlers;

namespace D365Sales.Adapter.Handlers;

/// <summary>
/// Business Central turned the quote into an order: close the opportunity as won with the order
/// amount. Ordering is guaranteed by the session (the customer event is processed first), but the
/// handler also makes sure the account is linked, so it stays correct even if an operator skipped
/// the customer event.
/// </summary>
public sealed class BcSalesOrderCreatedHandler(IDataverseClient dataverse, ILogger<BcSalesOrderCreatedHandler> logger)
    : IEventHandler<BcSalesOrderCreated>
{
    public async Task Handle(BcSalesOrderCreated message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        if (message.CrmAccountId is Guid accountId)
        {
            await dataverse.PatchAccountAsync(
                accountId,
                new Dictionary<string, object?>
                {
                    ["customertypecode"] = 3,
                    ["accountnumber"] = message.CustomerNumber,
                    ["cs_bccustomerid"] = message.CustomerId,
                    ["cs_masterdataowner"] = 2,
                },
                cancellationToken);
        }

        if (message.OpportunityId is not Guid opportunityId || opportunityId == Guid.Empty)
        {
            logger.LogInformation("Order {OrderNumber} has no CRM opportunity; nothing to close.", message.OrderNumber);
            return;
        }

        await dataverse.PatchOpportunityAsync(
            opportunityId,
            new Dictionary<string, object?>
            {
                ["cs_bcordernumber"] = message.OrderNumber,
                ["cs_bcquotestatus"] = "Accepted",
            },
            cancellationToken);

        await dataverse.WinOpportunityAsync(
            opportunityId,
            message.TotalAmountExcludingTax,
            message.OrderDate,
            $"Won: Business Central order {message.OrderNumber}",
            cancellationToken);

        logger.LogInformation("Opportunity {OpportunityId} won with Business Central order {OrderNumber}.", opportunityId, message.OrderNumber);
    }
}
