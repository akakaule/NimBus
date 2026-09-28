using D365Sales.Api.Data;
using D365Sales.Api.Domain;
using DynamicsBcDemo.Contracts.D365Sales;
using DynamicsBcDemo.Contracts.Demo;
using Microsoft.EntityFrameworkCore;
using NimBus.Core.Events;
using NimBus.SDK;

namespace D365Sales.Api.Integration;

/// <summary>A CRM change as it was published, kept so the demo can deliver it again.</summary>
public sealed record PublishedChange(IEvent Event, string SessionId, string CorrelationId, string MessageId, string Summary);

/// <summary>
/// Sends CRM's changes to Business Central: a prospect (<see cref="D365ProspectUpdated"/>) while CRM
/// still owns the account, and every opportunity change (<see cref="D365OpportunityUpdated"/>), both in
/// the account's session. Each message gets a deterministic MessageId from the record and its
/// ModifiedOn — <c>prospect:{account}:{ticks}</c>, <c>opportunity:{opportunity}:{ticks}</c> — so the
/// same change delivered twice is recognised by the Business Central adapter's inbox and skipped as
/// DuplicateDetected, while every real change is new. In production these messages come out of
/// Dataverse through a Service Endpoint and the NimBus Dataverse adapter; the contracts are the same.
/// </summary>
public sealed class CrmChangePublisher(D365DbContext db, IPublisherClient publisher, LastPublishedChange last, ILogger<CrmChangePublisher> logger)
{
    /// <summary>
    /// Publishes the prospect's current data. Returns the MessageId, or null when Business Central
    /// already manages the account (it has a quote), so CRM no longer sends its master data.
    /// </summary>
    public async Task<string?> PublishProspectAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.AccountId == accountId, cancellationToken)
            ?? throw new D365RuleException("The account does not exist.", StatusCodes.Status404NotFound);
        if (account.CsMasterDataOwner == OptionSets.MasterDataOwner.BusinessCentral)
            return null;

        var contact = account.PrimaryContactId is Guid contactId
            ? await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.ContactId == contactId, cancellationToken)
            : null;
        var @event = new D365ProspectUpdated
        {
            AccountId = account.AccountId,
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
            UpdatedAt = account.ModifiedOn,
        };

        var change = new PublishedChange(
            @event,
            account.AccountId.ToString(),
            account.AccountId.ToString(),
            $"prospect:{account.AccountId:N}:{account.ModifiedOn.UtcTicks}",
            $"prospect {account.Name}");
        await SendAsync(change, cancellationToken);
        return change.MessageId;
    }

    /// <summary>Publishes the opportunity's current header, so Business Central can quote it. Returns the MessageId.</summary>
    public async Task<string> PublishOpportunityAsync(Guid opportunityId, CancellationToken cancellationToken = default)
    {
        var opportunity = await db.Opportunities.AsNoTracking().FirstOrDefaultAsync(o => o.OpportunityId == opportunityId, cancellationToken)
            ?? throw new D365RuleException("The opportunity does not exist.", StatusCodes.Status404NotFound);
        var account = await db.Accounts.AsNoTracking().FirstAsync(a => a.AccountId == opportunity.CustomerId, cancellationToken);
        var owner = await db.SystemUsers.AsNoTracking().FirstAsync(u => u.SystemUserId == opportunity.OwnerId, cancellationToken);
        var productGroupCode = opportunity.CsProductGroupId is Guid productGroupId
            ? await db.ProductGroups.AsNoTracking().Where(g => g.ProductGroupId == productGroupId).Select(g => g.CsCode).FirstOrDefaultAsync(cancellationToken)
            : null;

        var @event = new D365OpportunityUpdated
        {
            AccountId = account.AccountId,
            OpportunityId = opportunity.OpportunityId,
            OpportunityNumber = opportunity.CsNumber,
            Name = opportunity.Name,
            AccountName = account.Name,
            BcCustomerId = account.CsBcCustomerId,
            SellerEmail = owner.InternalEmailAddress,
            SellerName = owner.FullName,
            EstimatedValue = opportunity.EstimatedValue,
            CurrencyCode = SeedData.CurrencyCode,
            EstimatedCloseDate = opportunity.EstimatedCloseDate,
            ProductGroupCode = productGroupCode,
            Status = opportunity.StateCode switch
            {
                OptionSets.State.WonOrQualified => D365OpportunityUpdated.Statuses.Won,
                OptionSets.State.LostOrDisqualified => D365OpportunityUpdated.Statuses.Lost,
                _ => D365OpportunityUpdated.Statuses.Open,
            },
            UpdatedAt = opportunity.ModifiedOn,
        };

        var change = new PublishedChange(
            @event,
            account.AccountId.ToString(),
            opportunity.OpportunityId.ToString(),
            $"opportunity:{opportunity.OpportunityId:N}:{opportunity.ModifiedOn.UtcTicks}",
            $"opportunity {opportunity.CsNumber} ({opportunity.Name})");
        await SendAsync(change, cancellationToken);
        last.Set(change);
        return change.MessageId;
    }

    /// <summary>
    /// Delivers the last published opportunity change again — same event, same MessageId — the way a
    /// source system's retry can. The Business Central adapter's inbox recognises and skips it. Null
    /// when nothing has been published since the start.
    /// </summary>
    public async Task<PublishedChange?> RedeliverLastOpportunityChangeAsync(CancellationToken cancellationToken = default)
    {
        var change = last.Get();
        if (change is null)
            return null;

        await SendAsync(change, cancellationToken);
        return change;
    }

    private async Task SendAsync(PublishedChange change, CancellationToken cancellationToken)
    {
        await publisher.Publish(change.Event, change.SessionId, change.CorrelationId, change.MessageId, cancellationToken);
        logger.LogInformation("Sent {Summary} to Business Central (MessageId {MessageId}).", change.Summary, change.MessageId);
    }
}

/// <summary>The last published opportunity change, kept in memory for the demo's redelivery.</summary>
public sealed class LastPublishedChange
{
    private readonly Lock _gate = new();
    private PublishedChange? _change;

    public void Set(PublishedChange change)
    {
        lock (_gate)
            _change = change;
    }

    public PublishedChange? Get()
    {
        lock (_gate)
            return _change;
    }
}
