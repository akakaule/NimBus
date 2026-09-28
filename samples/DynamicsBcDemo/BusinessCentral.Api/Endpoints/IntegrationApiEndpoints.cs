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
/// <item>a custom API (<c>/api/contoso/crm/v1.0/...</c>) for what the standard API can't do — keeping
/// CRM's prospects (as contacts) and opportunities, so BC users can quote them and link the quotes. In
/// a real tenant that is a small AL extension with API pages.</item>
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

        crm.MapPut("/prospects({crmAccountId:guid})", async (Guid crmAccountId, ProspectUpsert body, BcUnitOfWork uow, SalesService sales, CancellationToken ct) =>
        {
            var outcome = await uow.RunAsync(_ => sales.UpsertProspectAsync(crmAccountId, body.Prospect, body.ContactPerson, ct), ct);
            var result = new { crmAccountId, outcome = outcome.ToString() };
            return outcome == ProspectUpsertOutcome.Created
                ? Results.Created($"/api/contoso/crm/v1.0/companies({SeedData.CompanyId})/prospects({crmAccountId})", result)
                : Results.Ok(result);
        });

        crm.MapPut("/crmOpportunities({id:guid})", async (Guid id, CrmOpportunityData body, BcUnitOfWork uow, SalesService sales, ILogger<SalesService> logger, CancellationToken ct) =>
        {
            var outcome = await uow.RunAsync(_ => sales.UpsertCrmOpportunityAsync(id, body, ct), ct);
            logger.LogInformation("CRM opportunity {OpportunityNumber}: {Outcome}", body.Number, outcome);
            var result = new { id, number = body.Number, outcome = outcome.ToString() };
            return outcome == CrmOpportunityUpsertOutcome.Created
                ? Results.Created($"/api/contoso/crm/v1.0/companies({SeedData.CompanyId})/crmOpportunities({id})", result)
                : Results.Ok(result);
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

