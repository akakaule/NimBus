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

    /// <summary>Adds the seed data to <paramref name="db"/> (no save). Public for tests.</summary>
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

        foreach (var item in SeedData.Items)
        {
            db.Items.Add(new Item
            {
                Id = item.ItemId,
                Number = item.Number,
                DisplayName = item.DisplayName,
                UnitPrice = item.UnitPrice,
                BaseUnitOfMeasure = item.UnitOfMeasure,
                Blocked = item.BlockedInBc,
            });
        }

        foreach (var account in SeedData.Accounts.Where(a => a.BcCustomer is not null))
        {
            var bc = account.BcCustomer!;
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
                CrmAccountId = account.AccountId,
                Origin = (int)BcCustomerOrigin.CreatedInBc,
                LastModifiedDateTime = now,
            });
        }

        foreach (var opportunity in SeedData.Opportunities.Where(o => o.Quote is not null))
        {
            var sent = opportunity.Quote!;
            var account = SeedData.Accounts.Single(a => a.AccountId == opportunity.AccountId);
            var customer = account.BcCustomer
                ?? throw new InvalidOperationException($"Seeded quote {sent.Number} needs a BC customer.");
            var sentAt = now.AddDays(-sent.SentDaysAgo);

            var quote = new SalesQuote
            {
                Id = sent.QuoteId,
                Number = sent.Number,
                ExternalDocumentNumber = opportunity.Number,
                Description = opportunity.Name,
                DocumentDate = sentAt.UtcDateTime.Date.AddDays(-1),
                ValidUntilDate = sentAt.UtcDateTime.Date.AddDays(sent.ValidForDays),
                CustomerId = customer.CustomerId,
                CustomerNumber = customer.Number,
                SellToName = account.Name,
                SalespersonCode = SalespersonCodeOf(opportunity.OwnerId),
                Status = QuoteStatus.Sent,
                SentDate = sentAt,
                CurrencyCode = SeedData.CurrencyCode,
                CrmOpportunityId = opportunity.OpportunityId,
                CrmAccountId = opportunity.AccountId,
                CrmRequestRevision = 1,
                LastModifiedDateTime = sentAt,
            };

            var sequence = 10000;
            foreach (var (itemNumber, quantity) in opportunity.Lines)
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
    }

    private static string? SalespersonCodeOf(Guid systemUserId) =>
        SeedData.Sellers.FirstOrDefault(s => s.SystemUserId == systemUserId)?.BcSalespersonCode;
}
