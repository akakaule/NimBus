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

    /// <summary>
    /// Adds the seed data to <paramref name="db"/> (no save). Public for tests. CRM starts on go-live
    /// morning: prospects, opportunities and leads, but none of Business Central's customers, contacts
    /// or product groups yet — they arrive with the initial sync.
    /// </summary>
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

        foreach (var seed in SeedData.CrmAccounts)
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
                CsBcContactNumber = bc is null ? seed.BcCompanyContactNumber : null,
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
                // A customer's contacts belong to Business Central: the link lets the initial sync
                // update this contact instead of adding a second one.
                CsBcContactId = bc is null ? null : seed.PrimaryContact.BcContactId,
                CreatedOn = now.AddDays(-30),
            });

            if (bc is not null)
            {
                Timeline.Add(db, seed.AccountId, $"Existing Business Central customer {bc.Number}", "Business Central owns this account's master data.", Timeline.Integration, now.AddDays(-30));
            }
        }

        foreach (var seed in SeedData.Opportunities)
        {
            var account = SeedData.Accounts.Single(a => a.AccountId == seed.AccountId);
            db.Opportunities.Add(new Opportunity
            {
                OpportunityId = seed.OpportunityId,
                CsNumber = seed.Number,
                Name = seed.Name,
                CustomerId = seed.AccountId,
                ParentContactId = account.PrimaryContact.ContactId,
                EstimatedValue = seed.EstimatedValue,
                EstimatedCloseDate = now.UtcDateTime.Date.AddDays(seed.CloseInDays),
                CloseProbability = seed.Probability,
                StepName = seed.Stage,
                OwnerId = seed.OwnerId,
                CreatedOn = now.AddDays(-14),
                ModifiedOn = now,
            });
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
