using BusinessCentral.Api.Data;
using BusinessCentral.Api.Domain;
using BusinessCentral.Api.Integration;
using DynamicsBcDemo.Contracts.Demo;
using Microsoft.EntityFrameworkCore;

namespace BusinessCentral.Api.Endpoints;

/// <summary>
/// The surface the Business Central adapter calls, shaped like Business Central online:
/// <list type="bullet">
/// <item>standard API v2.0 routes (<c>/api/v2.0/companies({id})/customers(...)</c>) for reads;</item>
/// <item>a custom API (<c>/api/contoso/crm/v1.0/...</c>) for what the standard API can't do — quoting
/// a prospect contact and keeping CRM reference fields. In a real tenant that is a small AL
/// extension with API pages.</item>
/// </list>
/// Both answer 503/429 while the demo's fault toggles are on (see BcFaultInjectionMiddleware).
/// </summary>
public static class IntegrationApiEndpoints
{
    public static void MapIntegrationApiEndpoints(this IEndpointRouteBuilder app)
    {
        var v2 = app.MapGroup("/api/v2.0/companies({companyId:guid})")
            .AddEndpointFilter(CompanyFilter)
            .AddEndpointFilter(BusinessRuleFilter);

        v2.MapGet("/customers({id:guid})", async (Guid id, BcDbContext db, CancellationToken ct) =>
            await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) is { } customer
                ? Results.Ok(ToApiCustomer(customer))
                : NotFound($"Customer {id} does not exist."));

        v2.MapGet("/customers({id:guid})/customerFinancialDetail", async (Guid id, BcDbContext db, CancellationToken ct) =>
            await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) is { } customer
                ? Results.Ok(new
                {
                    id = customer.Id,
                    number = customer.Number,
                    balance = customer.BalanceDue,
                    overdueAmount = customer.OverdueAmount,
                    totalSalesExcludingTax = 0m,
                })
                : NotFound($"Customer {id} does not exist."));

        var crm = app.MapGroup("/api/contoso/crm/v1.0/companies({companyId:guid})")
            .AddEndpointFilter(CompanyFilter)
            .AddEndpointFilter(BusinessRuleFilter);

        crm.MapPost("/quoteRequests", async (QuoteRequest request, BcUnitOfWork uow, SalesService sales, ILogger<SalesService> logger, CancellationToken ct) =>
        {
            var result = await uow.RunAsync(events => sales.HandleQuoteRequestAsync(request, events, ct), ct);
            logger.LogInformation(
                "Quote request for opportunity {OpportunityNumber} (revision {Revision}): {Outcome} {QuoteNumber}",
                request.OpportunityNumber, request.RequestRevision, result.Outcome, result.Quote.Number);

            var body = new
            {
                id = result.Quote.Id,
                number = result.Quote.Number,
                status = result.Quote.Status,
                sellToContactNumber = result.Quote.SellToContactNumber,
                customerNumber = result.Quote.CustomerNumber,
                totalAmountExcludingTax = result.Quote.TotalAmountExcludingTax,
                outcome = result.Outcome.ToString(),
            };
            return result.Outcome == QuoteRequestOutcome.Created
                ? Results.Created($"/api/v2.0/companies({SeedData.CompanyId})/salesQuotes({result.Quote.Id})", body)
                : Results.Ok(body);
        });

        crm.MapPatch("/prospects({crmAccountId:guid})", async (Guid crmAccountId, ProspectPatch patch, BcUnitOfWork uow, SalesService sales, CancellationToken ct) =>
        {
            var outcome = await uow.RunAsync(_ => sales.UpdateProspectAsync(crmAccountId, patch.Prospect, patch.ContactPerson, ct), ct);
            return outcome == ProspectUpdateOutcome.NotFound
                ? NotFound($"No prospect contact is linked to CRM account {crmAccountId}.")
                : Results.Ok(new { crmAccountId, outcome = outcome.ToString() });
        });
    }

    /// <summary>API v2.0 JSON for a customer (camelCase, BC field names).</summary>
    private static object ToApiCustomer(Customer c) => new
    {
        id = c.Id,
        number = c.Number,
        displayName = c.DisplayName,
        type = c.Type,
        addressLine1 = c.AddressLine1,
        city = c.City,
        country = c.CountryCode,
        postalCode = c.PostalCode,
        phoneNumber = c.PhoneNumber,
        email = c.Email,
        website = c.Website,
        salespersonCode = c.SalespersonCode,
        balanceDue = c.BalanceDue,
        creditLimit = c.CreditLimit,
        taxRegistrationNumber = c.TaxRegistrationNumber,
        currencyCode = c.CurrencyCode,
        blocked = string.IsNullOrEmpty(c.Blocked) ? " " : c.Blocked,
        lastModifiedDateTime = c.LastModifiedDateTime,
    };

    private static IResult NotFound(string message) =>
        Results.Json(BcErrorBody.Of("Internal_RecordNotFound", message), statusCode: StatusCodes.Status404NotFound);

    private static async ValueTask<object?> CompanyFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var routeCompany = context.HttpContext.Request.RouteValues["companyId"]?.ToString();
        if (!Guid.TryParse(routeCompany, out var companyId) || companyId != SeedData.CompanyId)
        {
            return Results.Json(
                BcErrorBody.Of("Internal_CompanyNotFound", $"The company '{routeCompany}' does not exist."),
                statusCode: StatusCodes.Status404NotFound);
        }

        return await next(context);
    }

    internal static async ValueTask<object?> BusinessRuleFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (BcBusinessRuleException ex)
        {
            return Results.Json(BcErrorBody.Of(ex.Code, ex.Message), statusCode: ex.StatusCode);
        }
    }
}

/// <summary>Body of <c>PATCH .../prospects({crmAccountId})</c>.</summary>
public sealed record ProspectPatch(ProspectData Prospect, ContactPersonData? ContactPerson);
