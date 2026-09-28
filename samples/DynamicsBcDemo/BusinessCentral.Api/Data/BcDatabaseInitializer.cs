using BusinessCentral.Api.Domain;
using DynamicsBcDemo.Contracts.BusinessCentral;
using DynamicsBcDemo.Contracts.Demo;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using NimBus.Core.Outbox;
using NimBus.Outbox.SqlServer;

namespace BusinessCentral.Api.Data;

/// <summary>Creates the schema, the number series, the NimBus outbox table and the seed data.</summary>
public static class BcDatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, ILogger logger)
    {
        // Retry so the API survives SQL-container startup timing. If init ultimately fails, log and
        // keep going so Kestrel binds and the UI shows errors instead of connection refused.
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BcDbContext>();

                // Aspire pre-creates the database empty, which turns EnsureCreated into a no-op;
                // create the tables when this model's anchor table is missing instead.
                var creator = (IRelationalDatabaseCreator)db.GetService<IDatabaseCreator>();
                if (!await creator.ExistsAsync())
                    await creator.CreateAsync();
                var hasSchema = await db.Database.SqlQueryRaw<int>(
                    "SELECT CASE WHEN OBJECT_ID(N'dbo.Customers', N'U') IS NOT NULL THEN 1 ELSE 0 END AS Value").FirstAsync();
                if (hasSchema == 0)
                    await creator.CreateTablesAsync();

                await db.Database.ExecuteSqlRawAsync(@"
                    IF OBJECT_ID(N'dbo.ContactNo', N'SO') IS NULL CREATE SEQUENCE dbo.ContactNo AS BIGINT START WITH 101 INCREMENT BY 1;
                    IF OBJECT_ID(N'dbo.CustomerNo', N'SO') IS NULL CREATE SEQUENCE dbo.CustomerNo AS BIGINT START WITH 50 INCREMENT BY 10;
                    IF OBJECT_ID(N'dbo.SalesQuoteNo', N'SO') IS NULL CREATE SEQUENCE dbo.SalesQuoteNo AS BIGINT START WITH 1002 INCREMENT BY 1;
                    IF OBJECT_ID(N'dbo.SalesOrderNo', N'SO') IS NULL CREATE SEQUENCE dbo.SalesOrderNo AS BIGINT START WITH 1001 INCREMENT BY 1;");

                var outbox = (SqlServerOutbox)scope.ServiceProvider.GetRequiredService<IOutbox>();
                await outbox.EnsureTableExistsAsync();

                if (!await db.Customers.AnyAsync())
                {
                    Seed(db, DateTimeOffset.UtcNow);
                    await db.SaveChangesAsync();
                    logger.LogInformation("Seeded Business Central with the {CompanyName} demo data.", SeedData.CompanyName);
                }

                return;
            }
            catch (Exception ex) when (attempt < 10)
            {
                logger.LogWarning(ex, "Business Central startup init attempt {Attempt} failed: {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Business Central startup init did not complete after 10 attempts; continuing so Kestrel binds.");
            }
        }
    }

    /// <summary>
    /// Adds the seed data to <paramref name="db"/> (no save). Public for tests. BC starts with its
    /// customers, their contacts and the item categories; of CRM's records it only knows the prospect
    /// and opportunities the two systems already share.
    /// </summary>
    public static void Seed(BcDbContext db, DateTimeOffset now)
    {
        foreach (var seller in SeedData.Sellers.Where(s => s.BcSalespersonCode is not null))
        {
            db.Salespeople.Add(new Salesperson
            {
                Code = seller.BcSalespersonCode!,
                DisplayName = seller.FullName,
                Email = seller.Email.ToLowerInvariant(),
                CreatedAt = now,
            });
        }

        foreach (var category in SeedData.ItemCategories)
        {
            db.ItemCategories.Add(new ItemCategory
            {
                Id = category.ItemCategoryId,
                Code = category.Code,
                DisplayName = category.Description,
                LastModifiedDateTime = now,
            });
        }

        foreach (var item in SeedData.Items)
        {
            db.Items.Add(new Item
            {
                Id = item.ItemId,
                Number = item.Number,
                DisplayName = item.DisplayName,
                UnitPrice = item.UnitPrice,
                BaseUnitOfMeasure = item.UnitOfMeasure,
                ItemCategoryCode = item.CategoryCode,
                Blocked = item.BlockedInBc,
            });
        }

        foreach (var account in SeedData.Accounts)
        {
            var bc = account.BcCustomer;
            if (bc is not null)
            {
                db.Customers.Add(new Customer
                {
                    Id = bc.CustomerId,
                    Number = bc.Number,
                    DisplayName = account.Name,
                    AddressLine1 = account.AddressLine1,
                    City = account.City,
                    PostalCode = account.PostalCode,
                    CountryCode = account.CountryCode,
                    PhoneNumber = account.Phone,
                    Email = account.PrimaryContact.Email,
                    Website = account.Website,
                    TaxRegistrationNumber = account.VatRegistrationNumber,
                    SalespersonCode = SalespersonCodeOf(account.OwnerId),
                    CreditLimit = bc.CreditLimit,
                    BalanceDue = bc.BalanceDue,
                    OverdueAmount = bc.OverdueAmount,
                    Blocked = bc.Blocked,
                    PaymentTermsCode = bc.PaymentTermsCode,
                    CurrencyCode = SeedData.CurrencyCode,
                    // Only customers CRM already holds are linked; the rest reach CRM with the
                    // initial sync, and BC links them when their first CRM opportunity arrives.
                    CrmAccountId = account.InCrm ? account.AccountId : null,
                    Origin = (int)BcCustomerOrigin.CreatedInBc,
                    LastModifiedDateTime = now,
                });
            }

            // Every account BC knows is a company contact: a customer's, or a CRM prospect's.
            var company = new Contact
            {
                Id = account.BcCompanyContactId,
                Number = account.BcCompanyContactNumber,
                Type = ContactType.Company,
                DisplayName = account.Name,
                AddressLine1 = account.AddressLine1,
                City = account.City,
                PostalCode = account.PostalCode,
                CountryCode = account.CountryCode,
                PhoneNumber = account.Phone,
                Website = account.Website,
                VatRegistrationNumber = account.VatRegistrationNumber,
                CustomerTemplateCode = CustomerTemplates.DefaultFor(account.CountryCode).Code,
                CrmAccountId = bc is null ? account.AccountId : null,
                CustomerId = bc?.CustomerId,
                CustomerNumber = bc?.Number,
                LastModifiedDateTime = now,
            };
            db.Contacts.Add(company);

            if (bc is null)
            {
                // A prospect's contact person arrives from CRM with the prospect.
                company.ContactPersonName = $"{account.PrimaryContact.FirstName} {account.PrimaryContact.LastName}";
                company.ContactPersonEmail = account.PrimaryContact.Email;
                company.ContactPersonPhone = account.PrimaryContact.Phone;
                continue;
            }

            foreach (var person in account.OtherContacts.Prepend(account.PrimaryContact))
            {
                db.Contacts.Add(new Contact
                {
                    Id = person.BcContactId,
                    Number = person.BcContactNumber,
                    Type = ContactType.Person,
                    DisplayName = $"{person.FirstName} {person.LastName}",
                    CompanyContactId = account.BcCompanyContactId,
                    FirstName = person.FirstName,
                    Surname = person.LastName,
                    JobTitle = person.JobTitle,
                    Email = person.Email,
                    PhoneNumber = person.Phone,
                    City = account.City,
                    CountryCode = account.CountryCode,
                    LastModifiedDateTime = now,
                });
            }
        }

        foreach (var opportunity in SeedData.Opportunities)
        {
            var account = SeedData.Accounts.Single(a => a.AccountId == opportunity.AccountId);
            db.CrmOpportunities.Add(new CrmOpportunity
            {
                Id = opportunity.OpportunityId,
                Number = opportunity.Number,
                Name = opportunity.Name,
                CrmAccountId = account.AccountId,
                AccountName = account.Name,
                CustomerId = account.BcCustomer?.CustomerId,
                SalespersonCode = SalespersonCodeOf(opportunity.OwnerId),
                EstimatedValue = opportunity.EstimatedValue,
                CurrencyCode = SeedData.CurrencyCode,
                EstimatedCloseDate = now.UtcDateTime.Date.AddDays(opportunity.CloseInDays),
                Status = CrmOpportunityStatus.Open,
                LastModifiedDateTime = now,
            });
        }

        var bcQuote = SeedData.LitwareFrameworkQuote;
        var quoteAccount = SeedData.Accounts.Single(a => a.AccountId == bcQuote.AccountId);
        var quoteCustomer = quoteAccount.BcCustomer
            ?? throw new InvalidOperationException($"Seeded quote {bcQuote.Number} needs a BC customer.");
        var sentAt = now.AddDays(-bcQuote.SentDaysAgo);
        var quote = new SalesQuote
        {
            Id = bcQuote.QuoteId,
            Number = bcQuote.Number,
            Description = bcQuote.Description,
            DocumentDate = sentAt.UtcDateTime.Date.AddDays(-1),
            ValidUntilDate = sentAt.UtcDateTime.Date.AddDays(bcQuote.ValidForDays),
            CustomerId = quoteCustomer.CustomerId,
            CustomerNumber = quoteCustomer.Number,
            SellToName = quoteAccount.Name,
            SalespersonCode = SalespersonCodeOf(quoteAccount.OwnerId),
            Status = QuoteStatus.Sent,
            SentDate = sentAt,
            CurrencyCode = SeedData.CurrencyCode,
            LastModifiedDateTime = sentAt,
        };

        var sequence = 10000;
        foreach (var (itemNumber, quantity) in bcQuote.Lines)
        {
            var item = SeedData.Items.Single(i => i.Number == itemNumber);
            quote.Lines.Add(new SalesQuoteLine
            {
                Id = Guid.NewGuid(),
                QuoteId = quote.Id,
                Sequence = sequence,
                ItemNumber = item.Number,
                Description = item.DisplayName,
                Quantity = quantity,
                UnitPrice = item.UnitPrice,
                AmountExcludingTax = Math.Round(quantity * item.UnitPrice, 2),
            });
            sequence += 10000;
        }

        quote.TotalAmountExcludingTax = quote.Lines.Sum(l => l.AmountExcludingTax);
        db.SalesQuotes.Add(quote);
    }

    private static string? SalespersonCodeOf(Guid systemUserId) =>
        SeedData.Sellers.FirstOrDefault(s => s.SystemUserId == systemUserId)?.BcSalespersonCode;
}
