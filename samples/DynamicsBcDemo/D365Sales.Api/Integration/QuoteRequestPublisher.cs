using D365Sales.Api.Data;
using D365Sales.Api.Domain;
using DynamicsBcDemo.Contracts.D365Sales;
using DynamicsBcDemo.Contracts.Demo;
using Microsoft.EntityFrameworkCore;
using NimBus.SDK;

namespace D365Sales.Api.Integration;

/// <summary>What a quote request did.</summary>
public sealed record QuoteRequestResult(string MessageId, int Revision, bool Repeat);

/// <summary>
/// Sends <see cref="CreateBcSalesQuote"/> for an opportunity. Quotes are made in Business Central,
/// so CRM asks. The command gets a deterministic MessageId — <c>quote:{opportunity}:{lines
/// revision}</c> — so a double-clicked request is recognised by the Business Central adapter's inbox
/// and skipped as DuplicateDetected, while a request after the lines changed is a new request.
/// In production this command would come out of Dataverse through a Service Endpoint and the NimBus
/// Dataverse adapter; the contract is the same.
/// </summary>
public sealed class QuoteRequestPublisher(D365DbContext db, IPublisherClient publisher, TimeProvider clock, ILogger<QuoteRequestPublisher> logger)
{
    public async Task<QuoteRequestResult> RequestAsync(Guid opportunityId, Guid? requestedByUserId, CancellationToken cancellationToken = default)
    {
        var opportunity = await db.Opportunities.Include(o => o.Lines).FirstOrDefaultAsync(o => o.OpportunityId == opportunityId, cancellationToken)
            ?? throw new D365RuleException("The opportunity does not exist.", StatusCodes.Status404NotFound);
        if (opportunity.StateCode != OptionSets.State.Open)
            throw new D365RuleException("The opportunity is closed.", StatusCodes.Status409Conflict);
        if (opportunity.Lines.Count == 0)
            throw new D365RuleException("Add product lines before requesting a quote.");

        var account = await db.Accounts.FirstAsync(a => a.AccountId == opportunity.CustomerId, cancellationToken);
        var owner = await db.SystemUsers.FirstAsync(u => u.SystemUserId == opportunity.OwnerId, cancellationToken);
        var requestedBy = requestedByUserId is Guid userId
            ? await db.SystemUsers.FirstOrDefaultAsync(u => u.SystemUserId == userId, cancellationToken)
            : null;
        var contactId = opportunity.ParentContactId ?? account.PrimaryContactId;
        var contact = contactId is Guid id ? await db.Contacts.FirstOrDefaultAsync(c => c.ContactId == id, cancellationToken) : null;

        var now = clock.GetUtcNow();
        var revision = opportunity.CsLinesRevision;
        var command = new CreateBcSalesQuote
        {
            AccountId = account.AccountId,
            OpportunityId = opportunity.OpportunityId,
            OpportunityNumber = opportunity.CsNumber,
            OpportunityName = opportunity.Name,
            BcCustomerNumber = account.CustomerTypeCode == OptionSets.RelationshipType.Customer ? account.AccountNumber : null,
            Prospect = new ProspectDetails
            {
                Name = account.Name,
                VatRegistrationNumber = account.CsVatNumber,
                AddressLine1 = account.Address1Line1,
                City = account.Address1City,
                PostalCode = account.Address1PostalCode,
                CountryCode = account.Address1Country ?? string.Empty,
                Phone = account.Telephone1,
                Website = account.WebsiteUrl,
            },
            PrimaryContact = contact is null
                ? null
                : new ContactPersonDetails { FullName = contact.FullName, Email = contact.EmailAddress1, Phone = contact.Telephone1 },
            SellerEmail = owner.InternalEmailAddress,
            SellerName = owner.FullName,
            CurrencyCode = SeedData.CurrencyCode,
            Revision = revision,
            Lines = opportunity.Lines
                .OrderBy(l => l.Sequence)
                .Select(l => new QuoteRequestLine { ItemNumber = l.ProductNumber, Description = l.Description, Quantity = l.Quantity })
                .ToList(),
            RequestedAt = now,
        };

        var repeat = opportunity.CsQuoteRequestRevision == revision && opportunity.CsQuoteRequestedOn is not null;
        opportunity.CsQuoteRequestRevision = revision;
        opportunity.CsQuoteRequestedOn = now;
        if (opportunity.CsBcQuoteId is null)
            opportunity.CsBcQuoteStatus = "Requested";
        opportunity.ModifiedOn = now;
        Timeline.Add(
            db,
            opportunity.OpportunityId,
            repeat ? "Quote requested again from Business Central" : "Quote requested from Business Central",
            $"Revision {revision}, {opportunity.Lines.Count} line(s), requested by {requestedBy?.FullName ?? owner.FullName} for seller {owner.FullName}.",
            Timeline.User,
            now);
        await db.SaveChangesAsync(cancellationToken);

        var messageId = $"quote:{opportunity.OpportunityId:N}:{revision}";
        await publisher.Publish(command, account.AccountId.ToString(), opportunity.OpportunityId.ToString(), messageId, cancellationToken);
        logger.LogInformation(
            "Sent CreateBcSalesQuote for {OpportunityNumber} (revision {Revision}, MessageId {MessageId}{Repeat})",
            opportunity.CsNumber, revision, messageId, repeat ? ", repeat" : string.Empty);

        return new QuoteRequestResult(messageId, revision, repeat);
    }
}
