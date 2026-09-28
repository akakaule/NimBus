using BusinessCentral.Adapter.Clients;
using DynamicsBcDemo.Contracts.D365Sales;
using NimBus.SDK.EventHandlers;

namespace BusinessCentral.Adapter.Handlers;

/// <summary>
/// Makes a CRM opportunity available in Business Central, so a BC user can create the quote there and
/// link it to the opportunity. BC resolves the seller to a salesperson by e-mail; when the seller is not
/// a BC salesperson yet, BC refuses with an actionable message, the message fails and the account's
/// session waits until an operator has fixed the data in BC and resubmitted it.
/// </summary>
public sealed class D365OpportunityUpdatedHandler(IBusinessCentralClient bc, ILogger<D365OpportunityUpdatedHandler> logger)
    : IEventHandler<D365OpportunityUpdated>
{
    public async Task Handle(D365OpportunityUpdated message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        var body = new CrmOpportunityBody(
            Number: message.OpportunityNumber,
            Name: message.Name,
            CrmAccountId: message.AccountId,
            AccountName: message.AccountName,
            BcCustomerId: message.BcCustomerId,
            SellerEmail: message.SellerEmail,
            EstimatedValue: message.EstimatedValue,
            CurrencyCode: message.CurrencyCode,
            EstimatedCloseDate: message.EstimatedCloseDate,
            ProductGroupCode: message.ProductGroupCode,
            Status: message.Status);

        var response = await bc.UpsertCrmOpportunityAsync(message.OpportunityId, body, cancellationToken);
        logger.LogInformation(
            "CRM opportunity {OpportunityNumber} is available in Business Central ({Outcome}).",
            message.OpportunityNumber, response.Outcome);
    }
}
