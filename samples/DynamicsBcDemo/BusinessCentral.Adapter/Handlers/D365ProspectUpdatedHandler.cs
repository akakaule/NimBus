using BusinessCentral.Adapter.Clients;
using DynamicsBcDemo.Contracts.D365Sales;
using NimBus.SDK.EventHandlers;

namespace BusinessCentral.Adapter.Handlers;

/// <summary>
/// Keeps CRM's prospect as a Business Central contact, so BC users can quote it: BC creates the contact
/// the first time and updates it after that. Once the prospect has become a BC customer, BC owns the
/// master data: the change is ignored on purpose (and logged), which is the ownership rule enforced
/// where the data lives.
/// </summary>
public sealed class D365ProspectUpdatedHandler(IBusinessCentralClient bc, ILogger<D365ProspectUpdatedHandler> logger)
    : IEventHandler<D365ProspectUpdated>
{
    public async Task Handle(D365ProspectUpdated message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        var body = new ProspectUpsertBody(
            ToProspectBody(message.Prospect),
            message.PrimaryContact is { } person ? new ContactPersonBody(person.FullName, person.Email, person.Phone) : null);
        var result = await bc.UpsertProspectAsync(message.AccountId, body, cancellationToken);

        switch (result)
        {
            case ProspectUpsertResult.Created:
                logger.LogInformation("Business Central now holds CRM account {AccountId} as a prospect contact.", message.AccountId);
                break;
            case ProspectUpsertResult.Updated:
                logger.LogInformation("Updated the Business Central prospect for CRM account {AccountId}.", message.AccountId);
                break;
            case ProspectUpsertResult.OwnedByBusinessCentral:
                logger.LogInformation(
                    "Ignored CRM changes to account {AccountId}: it is a Business Central customer now, and Business Central owns its master data.",
                    message.AccountId);
                break;
        }
    }

    internal static ProspectBody ToProspectBody(ProspectDetails prospect) => new(
        prospect.Name,
        prospect.VatRegistrationNumber,
        prospect.AddressLine1,
        prospect.City,
        prospect.PostalCode,
        prospect.CountryCode,
        prospect.Phone,
        prospect.Website);
}
