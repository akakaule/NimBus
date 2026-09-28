using BusinessCentral.Adapter.Clients;
using DynamicsBcDemo.Contracts.D365Sales;
using NimBus.SDK.EventHandlers;

namespace BusinessCentral.Adapter.Handlers;

/// <summary>
/// Keeps the Business Central prospect contact in step with CRM while CRM still owns the prospect.
/// Once the prospect has become a BC customer, BC owns the master data: the update is ignored on
/// purpose (and logged), which is the ownership rule enforced where the data lives.
/// </summary>
public sealed class D365ProspectUpdatedHandler(IBusinessCentralClient bc, ILogger<D365ProspectUpdatedHandler> logger)
    : IEventHandler<D365ProspectUpdated>
{
    public async Task Handle(D365ProspectUpdated message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        var result = await bc.UpdateProspectAsync(
            message.AccountId,
            new ProspectPatchBody(CreateBcSalesQuoteHandler.ToProspectBody(message.Prospect), ContactPerson: null),
            cancellationToken);

        switch (result)
        {
            case ProspectUpdateResult.Updated:
                logger.LogInformation("Updated the Business Central prospect for CRM account {AccountId}.", message.AccountId);
                break;
            case ProspectUpdateResult.NotInBusinessCentral:
                logger.LogInformation("CRM account {AccountId} is not known in Business Central yet; nothing to update.", message.AccountId);
                break;
            case ProspectUpdateResult.OwnedByBusinessCentral:
                logger.LogInformation(
                    "Ignored CRM changes to account {AccountId}: it is a Business Central customer now, and Business Central owns its master data.",
                    message.AccountId);
                break;
        }
    }
}
