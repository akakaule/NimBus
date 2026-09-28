using System.Data;
using D365Sales.Api.Data;
using DynamicsBcDemo.Contracts.Demo;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace D365Sales.Api.Domain;

/// <summary>A product line as the seller edits it on the opportunity.</summary>
public sealed record OpportunityLineEdit(string ProductNumber, decimal Quantity);

/// <summary>Account fields a seller can edit.</summary>
public sealed record AccountEdit(
    string Name,
    string? Address1Line1,
    string? Address1City,
    string? Address1PostalCode,
    string? Address1Country,
    string? Telephone1,
    string? WebsiteUrl,
    string? CsVatNumber,
    Guid OwnerId,
    string? Description);

/// <summary>Opportunity fields a seller can edit.</summary>
public sealed record OpportunityEdit(string Name, DateTime? EstimatedCloseDate, int CloseProbability, string StepName);

/// <summary>
/// The CRM-side sales work: qualifying leads, editing opportunities and accounts. None of it talks
/// to Business Central except through the quote request and the prospect update, which callers
/// publish after saving.
/// </summary>
public sealed class SalesService(D365DbContext db, TimeProvider clock)
{
    private static readonly string[] Stages =
        [SeedData.Stages.Qualify, SeedData.Stages.Develop, SeedData.Stages.Propose, SeedData.Stages.Close];

    /// <summary>Qualifies a lead into a prospect account, a contact and an opportunity — CRM-only data.</summary>
    public async Task<Opportunity> QualifyLeadAsync(Guid leadId, Guid userId, CancellationToken cancellationToken = default)
    {
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.LeadId == leadId, cancellationToken)
            ?? throw new D365RuleException("The lead does not exist.", StatusCodes.Status404NotFound);
        if (lead.StateCode != OptionSets.State.Open)
            throw new D365RuleException("The lead is already qualified or disqualified.", StatusCodes.Status409Conflict);

        var now = clock.GetUtcNow();
        var user = await db.SystemUsers.FirstOrDefaultAsync(u => u.SystemUserId == userId, cancellationToken);

        var account = new Account
        {
            AccountId = lead.QualifiedAccountId ?? Guid.NewGuid(),
            Name = lead.CompanyName,
            CustomerTypeCode = OptionSets.RelationshipType.Prospect,
            Address1Line1 = lead.Address1Line1,
            Address1City = lead.Address1City,
            Address1PostalCode = lead.Address1PostalCode,
            Address1Country = lead.Address1Country,
            Telephone1 = lead.Telephone1,
            WebsiteUrl = lead.WebsiteUrl,
            CsVatNumber = lead.CsVatNumber,
            OwnerId = lead.OwnerId,
            CsMasterDataOwner = OptionSets.MasterDataOwner.Dynamics365,
            CreatedOn = now,
            ModifiedOn = now,
        };
        var contact = new Contact
        {
            ContactId = lead.QualifiedContactId ?? Guid.NewGuid(),
            FirstName = lead.FirstName,
            LastName = lead.LastName,
            EmailAddress1 = lead.EmailAddress1,
            Telephone1 = lead.Telephone1,
            JobTitle = lead.JobTitle,
            ParentCustomerId = account.AccountId,
            CreatedOn = now,
        };
        account.PrimaryContactId = contact.ContactId;

        var opportunity = new Opportunity
        {
            OpportunityId = lead.QualifiedOpportunityId ?? Guid.NewGuid(),
            CsNumber = lead.QualifiedOpportunityNumber ?? await NextOpportunityNumberAsync(cancellationToken),
            Name = lead.Subject,
            CustomerId = account.AccountId,
            ParentContactId = contact.ContactId,
            EstimatedValue = lead.EstimatedValue,
            EstimatedCloseDate = now.UtcDateTime.Date.AddDays(60),
            CloseProbability = 20,
            StepName = SeedData.Stages.Develop,
            OwnerId = lead.OwnerId,
            CreatedOn = now,
            ModifiedOn = now,
        };

        lead.StateCode = OptionSets.State.WonOrQualified;
        db.Accounts.Add(account);
        db.Contacts.Add(contact);
        db.Opportunities.Add(opportunity);

        var by = user?.FullName ?? "a seller";
        Timeline.Add(db, lead.LeadId, "Lead qualified", $"Qualified by {by} into {account.Name} ({opportunity.CsNumber}).", Timeline.User, now);
        Timeline.Add(db, account.AccountId, "Account created from lead", $"Relationship type Prospect. Owned by Dynamics 365 until the first order in Business Central.", Timeline.User, now);
        Timeline.Add(db, opportunity.OpportunityId, "Opportunity created from lead", $"Qualified by {by}. Add product lines, then request a quote from Business Central.", Timeline.User, now);

        await db.SaveChangesAsync(cancellationToken);
        return opportunity;
    }

    /// <summary>Replaces the product lines. Bumps the lines revision, so the next quote request is a new one.</summary>
    public async Task<Opportunity> SetLinesAsync(Guid opportunityId, IReadOnlyList<OpportunityLineEdit> lines, CancellationToken cancellationToken = default)
    {
        var opportunity = await LoadOpenOpportunityAsync(opportunityId, cancellationToken);
        var numbers = lines.Select(l => l.ProductNumber).Distinct().ToList();
        var products = await db.Products.Where(p => numbers.Contains(p.ProductNumber)).ToDictionaryAsync(p => p.ProductNumber, cancellationToken);
        var unknown = numbers.FirstOrDefault(n => !products.ContainsKey(n));
        if (unknown is not null)
            throw new D365RuleException($"Product {unknown} does not exist.");
        if (lines.Any(l => l.Quantity <= 0))
            throw new D365RuleException("Quantities must be greater than zero.");

        foreach (var old in opportunity.Lines.ToList())
        {
            opportunity.Lines.Remove(old);
            db.OpportunityProducts.Remove(old);
        }

        var sequence = 1;
        foreach (var line in lines)
        {
            var product = products[line.ProductNumber];
            var added = new OpportunityProduct
            {
                OpportunityProductId = Guid.NewGuid(),
                OpportunityId = opportunity.OpportunityId,
                Sequence = sequence++,
                ProductNumber = product.ProductNumber,
                Description = product.Name,
                Quantity = line.Quantity,
                PricePerUnit = product.Price,
                ExtendedAmount = Math.Round(line.Quantity * product.Price, 2),
            };
            opportunity.Lines.Add(added);
            // Add explicitly: found only through the tracked parent's collection, a row whose Guid
            // key is already set would be taken for an existing row and UPDATEd (0 rows affected).
            db.OpportunityProducts.Add(added);
        }

        var now = clock.GetUtcNow();
        opportunity.CsLinesRevision++;
        // Until Business Central has quoted, the pipeline value is CRM's own estimate from list
        // prices; once a quote exists, the estimate follows the real quote total instead.
        if (opportunity.CsBcQuoteId is null)
            opportunity.EstimatedValue = opportunity.Lines.Sum(l => l.ExtendedAmount);
        opportunity.ModifiedOn = now;
        Timeline.Add(db, opportunity.OpportunityId, "Product lines updated", $"{opportunity.Lines.Count} line(s), revision {opportunity.CsLinesRevision}.", Timeline.User, now);

        await db.SaveChangesAsync(cancellationToken);
        return opportunity;
    }

    /// <summary>Updates the opportunity header (CRM-owned).</summary>
    public async Task<Opportunity> UpdateOpportunityAsync(Guid opportunityId, OpportunityEdit edit, CancellationToken cancellationToken = default)
    {
        var opportunity = await LoadOpenOpportunityAsync(opportunityId, cancellationToken);
        if (!Stages.Contains(edit.StepName))
            throw new D365RuleException($"Unknown stage '{edit.StepName}'.");
        if (edit.CloseProbability is < 0 or > 100)
            throw new D365RuleException("Probability must be between 0 and 100.");

        opportunity.Name = edit.Name;
        opportunity.EstimatedCloseDate = edit.EstimatedCloseDate;
        opportunity.CloseProbability = edit.CloseProbability;
        opportunity.StepName = edit.StepName;
        opportunity.ModifiedOn = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return opportunity;
    }

    /// <summary>
    /// Updates an account. While CRM owns it (a prospect) every field is editable; once Business
    /// Central owns it (a customer) the master data is read-only here and must be changed in BC.
    /// Returns whether Business Central should hear about the change (it already knows the prospect).
    /// </summary>
    public async Task<(Account Account, bool NotifyBusinessCentral)> UpdateAccountAsync(Guid accountId, AccountEdit edit, CancellationToken cancellationToken = default)
    {
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.AccountId == accountId, cancellationToken)
            ?? throw new D365RuleException("The account does not exist.", StatusCodes.Status404NotFound);
        var now = clock.GetUtcNow();

        if (account.CsMasterDataOwner == OptionSets.MasterDataOwner.BusinessCentral)
        {
            var masterDataChanged =
                edit.Name != account.Name
                || edit.Address1Line1 != account.Address1Line1
                || edit.Address1City != account.Address1City
                || edit.Address1PostalCode != account.Address1PostalCode
                || edit.Address1Country != account.Address1Country
                || edit.Telephone1 != account.Telephone1
                || edit.WebsiteUrl != account.WebsiteUrl
                || edit.CsVatNumber != account.CsVatNumber;
            if (masterDataChanged)
            {
                throw new D365RuleException(
                    $"{account.Name} is a Business Central customer ({account.AccountNumber}); its master data is owned by Business Central. Change it there.",
                    StatusCodes.Status409Conflict);
            }

            account.OwnerId = edit.OwnerId;
            account.Description = edit.Description;
            account.ModifiedOn = now;
            await db.SaveChangesAsync(cancellationToken);
            return (account, false);
        }

        account.Name = edit.Name;
        account.Address1Line1 = edit.Address1Line1;
        account.Address1City = edit.Address1City;
        account.Address1PostalCode = edit.Address1PostalCode;
        account.Address1Country = edit.Address1Country;
        account.Telephone1 = edit.Telephone1;
        account.WebsiteUrl = edit.WebsiteUrl;
        account.CsVatNumber = edit.CsVatNumber;
        account.OwnerId = edit.OwnerId;
        account.Description = edit.Description;
        account.ModifiedOn = now;

        var knownToBc = account.CsBcContactNumber is not null;
        Timeline.Add(
            db,
            account.AccountId,
            "Account updated",
            knownToBc ? "Sent to Business Central, which holds this prospect as a contact." : "Prospect data stays in Dynamics 365.",
            Timeline.User,
            now);
        await db.SaveChangesAsync(cancellationToken);
        return (account, knownToBc);
    }

    /// <summary>The next opportunity number (cs_number autonumber), from a SQL sequence.</summary>
    public async Task<string> NextOpportunityNumberAsync(CancellationToken cancellationToken = default)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await db.Database.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT NEXT VALUE FOR dbo.OpportunityNo";
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        var value = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        return $"OPP-{value}";
    }

    private async Task<Opportunity> LoadOpenOpportunityAsync(Guid opportunityId, CancellationToken cancellationToken)
    {
        var opportunity = await db.Opportunities.Include(o => o.Lines).FirstOrDefaultAsync(o => o.OpportunityId == opportunityId, cancellationToken)
            ?? throw new D365RuleException("The opportunity does not exist.", StatusCodes.Status404NotFound);
        if (opportunity.StateCode != OptionSets.State.Open)
            throw new D365RuleException("The opportunity is closed.", StatusCodes.Status409Conflict);
        return opportunity;
    }
}
