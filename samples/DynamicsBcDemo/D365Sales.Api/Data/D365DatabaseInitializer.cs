using D365Sales.Api.Domain;
using DynamicsBcDemo.Contracts.Demo;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace D365Sales.Api.Data;

/// <summary>Creates the schema, the opportunity number series and the seed data.</summary>
public static class D365DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, ILogger logger)
    {
        // Retry so the API survives SQL-container startup timing; never fail startup, so Kestrel
        // binds and the UI shows errors instead of connection refused.
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<D365DbContext>();

                // Aspire pre-creates the database empty, which turns EnsureCreated into a no-op.
                var creator = (IRelationalDatabaseCreator)db.GetService<IDatabaseCreator>();
                if (!await creator.ExistsAsync())
                    await creator.CreateAsync();
                var hasSchema = await db.Database.SqlQueryRaw<int>(
                    "SELECT CASE WHEN OBJECT_ID(N'dbo.Accounts', N'U') IS NOT NULL THEN 1 ELSE 0 END AS Value").FirstAsync();
                if (hasSchema == 0)
                    await creator.CreateTablesAsync();

                // DDL can't take parameters; the start value is a compile-time constant.
                var createSequence =
                    "IF OBJECT_ID(N'dbo.OpportunityNo', N'SO') IS NULL CREATE SEQUENCE dbo.OpportunityNo AS BIGINT START WITH "
                    + SeedData.NextOpportunityNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " INCREMENT BY 1;";
                await db.Database.ExecuteSqlRawAsync(createSequence);

                if (!await db.Accounts.AnyAsync())
                {
                    Seed(db, DateTimeOffset.UtcNow);
                    await db.SaveChangesAsync();
                    logger.LogInformation("Seeded Dynamics 365 Sales with the {CompanyName} demo data.", SeedData.CompanyName);
                }

                return;
            }
            catch (Exception ex) when (attempt < 10)
            {
                logger.LogWarning(ex, "Dynamics 365 startup init attempt {Attempt} failed: {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Dynamics 365 startup init did not complete after 10 attempts; continuing so Kestrel binds.");
            }
        }
    }

    /// <summary>Adds the seed data to <paramref name="db"/> (no save). Public for tests.</summary>
    public static void Seed(D365DbContext db, DateTimeOffset now)
    {
        foreach (var seller in SeedData.Sellers)
        {
            db.SystemUsers.Add(new SystemUser
            {
                SystemUserId = seller.SystemUserId,
                FullName = seller.FullName,
                InternalEmailAddress = seller.Email,
            });
        }

        foreach (var item in SeedData.Items)
        {
            db.Products.Add(new Product
            {
                ProductId = item.ItemId,
                ProductNumber = item.Number,
                Name = item.DisplayName,
                Price = item.UnitPrice,
                DefaultUnit = item.UnitOfMeasure,
            });
        }

        foreach (var seed in SeedData.Accounts)
        {
            var bc = seed.BcCustomer;
            db.Accounts.Add(new Account
            {
                AccountId = seed.AccountId,
                Name = seed.Name,
                AccountNumber = bc?.Number,
                CustomerTypeCode = bc is null ? OptionSets.RelationshipType.Prospect : OptionSets.RelationshipType.Customer,
                Address1Line1 = seed.AddressLine1,
                Address1City = seed.City,
                Address1PostalCode = seed.PostalCode,
                Address1Country = seed.CountryCode,
                Telephone1 = seed.Phone,
                WebsiteUrl = seed.Website,
                CsVatNumber = seed.VatRegistrationNumber,
                CreditLimit = bc?.CreditLimit,
                CreditOnHold = bc is not null && !string.IsNullOrEmpty(bc.Blocked),
                OwnerId = seed.OwnerId,
                PrimaryContactId = seed.PrimaryContact.ContactId,
                CsBcCustomerId = bc?.CustomerId,
                CsBcBlocked = bc?.Blocked,
                CsBcBalanceDue = bc?.BalanceDue,
                CsBcPaymentTerms = bc?.PaymentTermsCode,
                CsMasterDataOwner = bc is null ? OptionSets.MasterDataOwner.Dynamics365 : OptionSets.MasterDataOwner.BusinessCentral,
                CsBcLastSyncedOn = bc is null ? null : now,
                CreatedOn = now.AddDays(-30),
                ModifiedOn = now,
            });

            db.Contacts.Add(new Contact
            {
                ContactId = seed.PrimaryContact.ContactId,
                FirstName = seed.PrimaryContact.FirstName,
                LastName = seed.PrimaryContact.LastName,
                EmailAddress1 = seed.PrimaryContact.Email,
                Telephone1 = seed.PrimaryContact.Phone,
                JobTitle = seed.PrimaryContact.JobTitle,
                ParentCustomerId = seed.AccountId,
                CreatedOn = now.AddDays(-30),
            });

            if (bc is not null)
            {
                Timeline.Add(db, seed.AccountId, $"Existing Business Central customer {bc.Number}", "Loaded into Dynamics 365 at go-live. Business Central owns this account's master data.", Timeline.Integration, now.AddDays(-30));
            }
        }

        foreach (var seed in SeedData.Opportunities)
        {
            var account = SeedData.Accounts.Single(a => a.AccountId == seed.AccountId);
            var opportunity = new Opportunity
            {
                OpportunityId = seed.OpportunityId,
                CsNumber = seed.Number,
                Name = seed.Name,
                CustomerId = seed.AccountId,
                ParentContactId = account.PrimaryContact.ContactId,
                EstimatedCloseDate = now.UtcDateTime.Date.AddDays(seed.CloseInDays),
                CloseProbability = seed.Probability,
                StepName = seed.Stage,
                OwnerId = seed.OwnerId,
                CsLinesRevision = 1,
                CreatedOn = now.AddDays(-14),
                ModifiedOn = now,
            };

            var sequence = 1;
            foreach (var (itemNumber, quantity) in seed.Lines)
            {
                var item = SeedData.Items.Single(i => i.Number == itemNumber);
                opportunity.Lines.Add(new OpportunityProduct
                {
                    OpportunityProductId = Guid.NewGuid(),
                    OpportunityId = seed.OpportunityId,
                    Sequence = sequence++,
                    ProductNumber = item.Number,
                    Description = item.DisplayName,
                    Quantity = quantity,
                    PricePerUnit = item.UnitPrice,
                    ExtendedAmount = Math.Round(quantity * item.UnitPrice, 2),
                });
            }

            opportunity.EstimatedValue = opportunity.Lines.Sum(l => l.ExtendedAmount);

            if (seed.Quote is { } quote)
            {
                // Same pricing as the BC seed, so the mirror and the BC quote agree to the cent.
                var sentAt = now.AddDays(-quote.SentDaysAgo);
                opportunity.CsBcQuoteId = quote.QuoteId;
                opportunity.CsBcQuoteNumber = quote.Number;
                opportunity.CsBcQuoteStatus = "Sent";
                opportunity.CsQuoteRequestRevision = 1;
                opportunity.CsQuoteRequestedOn = sentAt.AddDays(-1);
                db.BcQuotes.Add(new BcQuoteMirror
                {
                    BcQuoteId = quote.QuoteId,
                    QuoteNumber = quote.Number,
                    OpportunityId = seed.OpportunityId,
                    AccountId = seed.AccountId,
                    Status = "Sent",
                    TotalAmount = opportunity.EstimatedValue.Value,
                    CurrencyCode = SeedData.CurrencyCode,
                    ValidUntil = sentAt.UtcDateTime.Date.AddDays(quote.ValidForDays),
                    SentOn = sentAt,
                    LastSyncedOn = sentAt,
                });
                Timeline.Add(db, seed.OpportunityId, $"Business Central quote {quote.Number} is now Sent", $"{Timeline.Money(opportunity.EstimatedValue.Value)}.", Timeline.Integration, sentAt);
            }

            db.Opportunities.Add(opportunity);
        }

        foreach (var seed in SeedData.Leads)
        {
            db.Leads.Add(new Lead
            {
                LeadId = seed.LeadId,
                Subject = seed.Topic,
                FirstName = seed.FirstName,
                LastName = seed.LastName,
                CompanyName = seed.CompanyName,
                EmailAddress1 = seed.Email,
                Telephone1 = seed.Phone,
                JobTitle = seed.JobTitle,
                Address1Line1 = seed.AddressLine1,
                Address1City = seed.City,
                Address1PostalCode = seed.PostalCode,
                Address1Country = seed.CountryCode,
                WebsiteUrl = seed.Website,
                CsVatNumber = seed.VatRegistrationNumber,
                EstimatedValue = seed.EstimatedValue,
                OwnerId = seed.OwnerId,
                StateCode = OptionSets.State.Open,
                CreatedOn = now.AddDays(-2),
                QualifiedAccountId = seed.QualifiedAccountId,
                QualifiedContactId = seed.QualifiedContactId,
                QualifiedOpportunityId = seed.QualifiedOpportunityId,
                QualifiedOpportunityNumber = seed.QualifiedOpportunityNumber,
            });
        }
    }
}
