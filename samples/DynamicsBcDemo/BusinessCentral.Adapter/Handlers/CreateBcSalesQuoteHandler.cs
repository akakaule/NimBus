using BusinessCentral.Adapter.Clients;
using DynamicsBcDemo.Contracts.D365Sales;
using NimBus.SDK.EventHandlers;

namespace BusinessCentral.Adapter.Handlers;

/// <summary>
/// Creates the Business Central quote for a CRM opportunity. BC does the work in one transaction
/// (find or create the prospect contact, resolve the salesperson, price the lines) and is
/// idempotent per opportunity, so an operator resubmit or a repeated request never creates a
/// second quote. The resulting BcSalesQuoteCreated comes back from BC's outbox.
/// </summary>
public sealed class CreateBcSalesQuoteHandler(IBusinessCentralClient bc, ILogger<CreateBcSalesQuoteHandler> logger)
    : IEventHandler<CreateBcSalesQuote>
{
    public async Task Handle(CreateBcSalesQuote message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        var body = new QuoteRequestBody(
            CrmOpportunityId: message.OpportunityId,
            CrmAccountId: message.AccountId,
            OpportunityNumber: message.OpportunityNumber,
            OpportunityName: message.OpportunityName,
            RequestRevision: message.Revision,
            CustomerNumber: message.BcCustomerNumber,
            Prospect: ToProspectBody(message.Prospect),
            ContactPerson: message.PrimaryContact is { } person
                ? new ContactPersonBody(person.FullName, person.Email, person.Phone)
                : null,
            SellerEmail: message.SellerEmail,
            CurrencyCode: message.CurrencyCode,
            Lines: message.Lines.Select(l => new QuoteRequestLineBody(l.ItemNumber, l.Quantity, l.Description)).ToList());

        var response = await bc.CreateQuoteRequestAsync(body, cancellationToken);
        logger.LogInformation(
            "Quote request for opportunity {OpportunityNumber} (revision {Revision}): {Outcome} Business Central quote {QuoteNumber}",
            message.OpportunityNumber, message.Revision, response.Outcome, response.Number);
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
