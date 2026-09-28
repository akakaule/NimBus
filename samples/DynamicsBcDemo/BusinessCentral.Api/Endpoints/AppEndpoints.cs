using BusinessCentral.Api.Data;
using BusinessCentral.Api.Domain;
using BusinessCentral.Api.Integration;
using DynamicsBcDemo.Contracts.Demo;
using Microsoft.EntityFrameworkCore;

namespace BusinessCentral.Api.Endpoints;

/// <summary>
/// The plain JSON surface the Business Central look-alike web client uses. Writes go through
/// <see cref="SalesService"/> and <see cref="BcUnitOfWork"/>, so every change a BC user makes is
/// published to CRM exactly like the integration API's changes.
/// </summary>
public static class AppEndpoints
{
    public static void MapAppEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/app").AddEndpointFilter(IntegrationApiEndpoints.BusinessRuleFilter);

        api.MapGet("/info", () => Results.Ok(new
        {
            companyName = SeedData.CompanyName,
            companyId = SeedData.CompanyId,
            environment = "Production (simulated)",
        }));

        api.MapGet("/rolecenter", async (BcDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
            var quotes = await db.SalesQuotes.AsNoTracking().ToListAsync(ct);
            var orders = await db.SalesOrders.AsNoTracking().ToListAsync(ct);
            var recentQuotes = quotes.Select(q => new RecentDocument("Sales Quote", q.Id, q.Number, q.SellToName, q.Status, q.TotalAmountExcludingTax, q.CurrencyCode, q.LastModifiedDateTime));
            var recentOrders = orders.Select(o => new RecentDocument("Sales Order", o.Id, o.Number, o.CustomerName, o.Status, o.TotalAmountExcludingTax, o.CurrencyCode, o.LastModifiedDateTime));

            return Results.Ok(new
            {
                openQuotes = quotes.Count(q => q.Status == QuoteStatus.Draft),
                sentQuotes = quotes.Count(q => q.Status == QuoteStatus.Sent),
                openQuotesValue = quotes.Where(q => q.Status is QuoteStatus.Draft or QuoteStatus.Sent).Sum(q => q.TotalAmountExcludingTax),
                ordersThisMonth = orders.Count(o => o.OrderDate >= monthStart),
                ordersValueThisMonth = orders.Where(o => o.OrderDate >= monthStart).Sum(o => o.TotalAmountExcludingTax),
                customers = await db.Customers.CountAsync(ct),
                customersBlocked = await db.Customers.CountAsync(c => c.Blocked != string.Empty, ct),
                prospects = await db.Contacts.CountAsync(c => c.CustomerId == null, ct),
                recentDocuments = recentQuotes.Concat(recentOrders).OrderByDescending(d => d.LastModified).Take(8),
            });
        });

        // ---- Sales quotes ---------------------------------------------------------------------

        api.MapGet("/quotes", async (BcDbContext db, CancellationToken ct) =>
            Results.Ok((await db.SalesQuotes.AsNoTracking().Include(q => q.Lines)
                    .OrderByDescending(q => q.LastModifiedDateTime).ToListAsync(ct))
                .Select(QuoteSummary)));

        api.MapGet("/quotes/{id:guid}", async (Guid id, BcDbContext db, CancellationToken ct) =>
            await QuoteDetailAsync(db, id, ct) is { } detail ? Results.Ok(detail) : Results.NotFound());

        api.MapPut("/quotes/{id:guid}/lines", async (Guid id, QuoteLinesBody body, BcUnitOfWork uow, SalesService sales, BcDbContext db, CancellationToken ct) =>
        {
            await uow.RunAsync(events => sales.UpdateLinesAsync(id, body.Lines, events, ct), ct);
            return Results.Ok(await QuoteDetailAsync(db, id, ct));
        });

        api.MapPost("/quotes/{id:guid}/send", async (Guid id, BcUnitOfWork uow, SalesService sales, BcDbContext db, CancellationToken ct) =>
        {
            await uow.RunAsync(events => sales.SendAsync(id, events, ct), ct);
            return Results.Ok(await QuoteDetailAsync(db, id, ct));
        });

        api.MapPost("/quotes/{id:guid}/make-order", async (Guid id, MakeOrderBody? body, BcUnitOfWork uow, SalesService sales, CancellationToken ct) =>
        {
            var result = await uow.RunAsync(events => sales.MakeOrderAsync(id, body?.CustomerTemplateCode, events, ct), ct);
            return Results.Ok(new
            {
                orderId = result.Order.Id,
                orderNumber = result.Order.Number,
                customerId = result.Customer.Id,
                customerNumber = result.Customer.Number,
                customerCreated = result.CustomerCreated,
            });
        });

        // ---- Customers, contacts, salespeople, items, orders ------------------------------------

        api.MapGet("/customers", async (BcDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Customers.AsNoTracking().OrderBy(c => c.Number).ToListAsync(ct)));

        api.MapGet("/customers/{id:guid}", async (Guid id, BcDbContext db, CancellationToken ct) =>
        {
            var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
            if (customer is null) return Results.NotFound();

            var quotes = await db.SalesQuotes.AsNoTracking().Include(q => q.Lines)
                .Where(q => q.CustomerId == id).OrderByDescending(q => q.LastModifiedDateTime).ToListAsync(ct);
            var orders = await db.SalesOrders.AsNoTracking()
                .Where(o => o.CustomerId == id).OrderByDescending(o => o.OrderDate).ToListAsync(ct);
            return Results.Ok(new { customer, quotes = quotes.Select(QuoteSummary), orders });
        });

        api.MapPut("/customers/{id:guid}", async (Guid id, CustomerEdit edit, BcUnitOfWork uow, SalesService sales, CancellationToken ct) =>
            Results.Ok(await uow.RunAsync(events => sales.UpdateCustomerAsync(id, edit, events, ct), ct)));

        api.MapGet("/contacts", async (BcDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Contacts.AsNoTracking().OrderByDescending(c => c.Number).ToListAsync(ct)));

        api.MapGet("/salespeople", async (BcDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Salespeople.AsNoTracking().OrderBy(s => s.Code).ToListAsync(ct)));

        api.MapPost("/salespeople", async (SalespersonBody body, BcDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var code = body.Code.Trim().ToUpperInvariant();
            var email = body.Email.Trim().ToLowerInvariant();
            if (code.Length is 0 or > 20 || string.IsNullOrWhiteSpace(body.DisplayName) || !email.Contains('@', StringComparison.Ordinal))
                throw new BcBusinessRuleException("Application_InvalidSalesperson", "Code (max 20 characters), name and e-mail are required.", StatusCodes.Status400BadRequest);
            if (await db.Salespeople.AnyAsync(s => s.Code == code, ct))
                throw new BcBusinessRuleException("Application_SalespersonExists", $"Salesperson {code} already exists.", StatusCodes.Status409Conflict);
            if (await db.Salespeople.AnyAsync(s => s.Email == email, ct))
                throw new BcBusinessRuleException("Application_SalespersonExists", $"A salesperson with e-mail {email} already exists.", StatusCodes.Status409Conflict);

            var salesperson = new Salesperson { Code = code, DisplayName = body.DisplayName.Trim(), Email = email, CreatedAt = clock.GetUtcNow() };
            db.Salespeople.Add(salesperson);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/app/salespeople/{code}", salesperson);
        });

        api.MapGet("/items", async (BcDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Items.AsNoTracking().OrderBy(i => i.Number).ToListAsync(ct)));

        api.MapGet("/orders", async (BcDbContext db, CancellationToken ct) =>
            Results.Ok(await db.SalesOrders.AsNoTracking().OrderByDescending(o => o.LastModifiedDateTime).ToListAsync(ct)));

        api.MapGet("/orders/{id:guid}", async (Guid id, BcDbContext db, CancellationToken ct) =>
        {
            var order = await db.SalesOrders.AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct);
            if (order is null) return Results.NotFound();
            order.Lines = order.Lines.OrderBy(l => l.Sequence).ToList();
            return Results.Ok(order);
        });

        api.MapGet("/templates", () => Results.Ok(CustomerTemplates.All));
    }

    private static object QuoteSummary(SalesQuote q) => new
    {
        q.Id,
        q.Number,
        q.SellToName,
        sellToType = q.CustomerId is null ? "Contact" : "Customer",
        sellToNumber = q.CustomerNumber ?? q.SellToContactNumber,
        q.ExternalDocumentNumber,
        q.Description,
        q.SalespersonCode,
        q.Status,
        q.TotalAmountExcludingTax,
        q.CurrencyCode,
        q.DocumentDate,
        q.ValidUntilDate,
        q.OrderNumber,
        lineCount = q.Lines.Count,
        lastModified = q.LastModifiedDateTime,
    };

    private static async Task<object?> QuoteDetailAsync(BcDbContext db, Guid id, CancellationToken ct)
    {
        var quote = await db.SalesQuotes.AsNoTracking().Include(q => q.Lines).FirstOrDefaultAsync(q => q.Id == id, ct);
        if (quote is null) return null;

        var contact = quote.SellToContactId is Guid contactId
            ? await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contactId, ct)
            : null;
        var customer = quote.CustomerId is Guid customerId
            ? await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId, ct)
            : null;

        return new
        {
            quote.Id,
            quote.Number,
            quote.Description,
            quote.ExternalDocumentNumber,
            quote.DocumentDate,
            quote.ValidUntilDate,
            quote.Status,
            quote.SentDate,
            quote.AcceptedDate,
            quote.SalespersonCode,
            quote.CurrencyCode,
            quote.TotalAmountExcludingTax,
            quote.OrderNumber,
            quote.SellToName,
            quote.CrmOpportunityId,
            quote.CrmAccountId,
            quote.LastModifiedDateTime,
            sellToType = customer is null ? "Contact" : "Customer",
            contact,
            customer,
            lines = quote.Lines.OrderBy(l => l.Sequence),
            proposedTemplate = contact is null ? null : CustomerTemplates.Find(contact.CustomerTemplateCode) ?? CustomerTemplates.DefaultFor(contact.CountryCode),
        };
    }

    private sealed record RecentDocument(string Type, Guid Id, string Number, string Name, string Status, decimal Amount, string CurrencyCode, DateTimeOffset LastModified);
}

public sealed record QuoteLinesBody(IReadOnlyList<QuoteLineEdit> Lines);

public sealed record MakeOrderBody(string? CustomerTemplateCode);

public sealed record SalespersonBody(string Code, string DisplayName, string Email);
