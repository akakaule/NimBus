using D365Sales.Api.Data;
using D365Sales.Api.Domain;
using D365Sales.Api.Integration;
using DynamicsBcDemo.Contracts.BusinessCentral;
using DynamicsBcDemo.Contracts.D365Sales;
using DynamicsBcDemo.Contracts.Demo;
using Microsoft.EntityFrameworkCore;
using NimBus.Core.Messages.Exceptions;
using NimBus.SDK;

namespace D365Sales.Api.Endpoints;

/// <summary>
/// The plain JSON surface of the Sales Hub look-alike. Only writes made here — by a seller — are
/// published to NimBus (<see cref="CrmChangePublisher"/>). Writes made by the integration arrive on the
/// Dataverse-shaped API and are never published back, which is what prevents echo loops.
/// </summary>
public static class AppEndpoints
{
    private static readonly TimeSpan CreditCheckTimeout = TimeSpan.FromSeconds(10);

    public static void MapAppEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/app").AddEndpointFilter(RuleFilter);

        api.MapGet("/users", async (D365DbContext db, CancellationToken ct) =>
            Results.Ok(await db.SystemUsers.AsNoTracking().OrderBy(u => u.FullName).ToListAsync(ct)));

        api.MapGet("/productgroups", async (D365DbContext db, CancellationToken ct) =>
            Results.Ok(await db.ProductGroups.AsNoTracking().OrderBy(g => g.Name).ToListAsync(ct)));

        api.MapGet("/dashboard", async (D365DbContext db, CancellationToken ct) =>
        {
            var open = await db.Opportunities.AsNoTracking().Where(o => o.StateCode == OptionSets.State.Open).ToListAsync(ct);
            var won = await db.Opportunities.AsNoTracking().Where(o => o.StateCode == OptionSets.State.WonOrQualified).ToListAsync(ct);
            var activity = await db.Timeline.AsNoTracking()
                .Where(t => t.Source == Timeline.Integration)
                .OrderByDescending(t => t.CreatedOn).Take(10).ToListAsync(ct);
            return Results.Ok(new
            {
                pipeline = new[] { SeedData.Stages.Qualify, SeedData.Stages.Develop, SeedData.Stages.Propose, SeedData.Stages.Close }
                    .Select(stage => new
                    {
                        stage,
                        count = open.Count(o => o.StepName == stage),
                        value = open.Where(o => o.StepName == stage).Sum(o => o.EstimatedValue ?? 0m),
                    }),
                openCount = open.Count,
                openValue = open.Sum(o => o.EstimatedValue ?? 0m),
                wonCount = won.Count,
                wonValue = won.Sum(o => o.ActualValue ?? 0m),
                quotesInBc = open.Count(o => o.CsBcQuoteNumber is not null),
                integrationActivity = activity,
            });
        });

        // ---- Leads -------------------------------------------------------------------------------

        api.MapGet("/leads", async (D365DbContext db, CancellationToken ct) =>
        {
            var users = await db.SystemUsers.AsNoTracking().ToDictionaryAsync(u => u.SystemUserId, u => u.FullName, ct);
            var leads = await db.Leads.AsNoTracking().OrderBy(l => l.StateCode).ThenByDescending(l => l.CreatedOn).ToListAsync(ct);
            return Results.Ok(leads.Select(l => new { lead = l, owner = users.GetValueOrDefault(l.OwnerId) }));
        });

        api.MapGet("/leads/{id:guid}", async (Guid id, D365DbContext db, CancellationToken ct) =>
        {
            var lead = await db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.LeadId == id, ct);
            if (lead is null) return Results.NotFound();
            var timeline = await db.Timeline.AsNoTracking().Where(t => t.RegardingId == id).OrderByDescending(t => t.CreatedOn).ToListAsync(ct);
            return Results.Ok(new { lead, timeline });
        });

        // Qualifying sends the new prospect, then its opportunity, to Business Central — in the
        // account's session, so BC always knows the prospect before the opportunity arrives.
        api.MapPost("/leads/{id:guid}/qualify", async (Guid id, UserBody body, SalesService sales, CrmChangePublisher changes, CancellationToken ct) =>
        {
            var opportunity = await sales.QualifyLeadAsync(id, body.UserId, ct);
            await changes.PublishProspectAsync(opportunity.CustomerId, ct);
            await changes.PublishOpportunityAsync(opportunity.OpportunityId, ct);
            return Results.Ok(new { opportunityId = opportunity.OpportunityId, accountId = opportunity.CustomerId });
        });

        // ---- Accounts ----------------------------------------------------------------------------

        api.MapGet("/accounts", async (D365DbContext db, CancellationToken ct) =>
        {
            var users = await db.SystemUsers.AsNoTracking().ToDictionaryAsync(u => u.SystemUserId, u => u.FullName, ct);
            var accounts = await db.Accounts.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct);
            return Results.Ok(accounts.Select(a => new { account = a, owner = users.GetValueOrDefault(a.OwnerId) }));
        });

        api.MapGet("/accounts/{id:guid}", async (Guid id, D365DbContext db, CancellationToken ct) =>
        {
            var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.AccountId == id, ct);
            if (account is null) return Results.NotFound();
            var contacts = await db.Contacts.AsNoTracking().Where(c => c.ParentCustomerId == id).ToListAsync(ct);
            var opportunities = await db.Opportunities.AsNoTracking().Where(o => o.CustomerId == id).OrderByDescending(o => o.ModifiedOn).ToListAsync(ct);
            var timeline = await db.Timeline.AsNoTracking().Where(t => t.RegardingId == id).OrderByDescending(t => t.CreatedOn).ToListAsync(ct);
            var owner = await db.SystemUsers.AsNoTracking().FirstOrDefaultAsync(u => u.SystemUserId == account.OwnerId, ct);
            return Results.Ok(new
            {
                account,
                owner,
                contacts = contacts.Select(c => new { c.ContactId, c.FullName, c.EmailAddress1, c.Telephone1, c.JobTitle }),
                opportunities,
                timeline,
            });
        });

        api.MapPut("/accounts/{id:guid}", async (Guid id, AccountEdit edit, SalesService sales, CrmChangePublisher changes, CancellationToken ct) =>
        {
            var (account, notifyBc) = await sales.UpdateAccountAsync(id, edit, ct);
            if (notifyBc)
                await changes.PublishProspectAsync(account.AccountId, ct);

            return Results.Ok(account);
        });

        // Request/reply: a live question to Business Central, answered on D365SalesEndpoint-reply.
        api.MapPost("/accounts/{id:guid}/credit-check", async (Guid id, UserBody body, D365DbContext db, IPublisherClient publisher, ILoggerFactory lf, CancellationToken ct) =>
        {
            var logger = lf.CreateLogger("D365Sales.Api.CreditCheck");
            var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.AccountId == id, ct);
            if (account is null) return Results.NotFound();
            if (account.CsBcCustomerId is not Guid bcCustomerId)
            {
                return Results.Json(
                    new { error = $"{account.Name} is a prospect. Business Central has no credit data until the first order makes it a customer." },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var user = await db.SystemUsers.AsNoTracking().FirstOrDefaultAsync(u => u.SystemUserId == body.UserId, ct);
            try
            {
                var status = await publisher.Request<D365CreditCheckRequested, BcCreditStatus>(
                    new D365CreditCheckRequested
                    {
                        AccountId = account.AccountId,
                        BcCustomerId = bcCustomerId,
                        BcCustomerNumber = account.AccountNumber,
                        RequestedBy = user?.InternalEmailAddress,
                        RequestedAt = DateTimeOffset.UtcNow,
                    },
                    CreditCheckTimeout,
                    ct);
                return Results.Ok(status);
            }
            catch (TimeoutException)
            {
                logger.LogWarning("Credit check for {AccountName} timed out after {Timeout}s", account.Name, CreditCheckTimeout.TotalSeconds);
                return Results.Json(
                    new { error = $"Business Central did not answer within {CreditCheckTimeout.TotalSeconds:0} seconds." },
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (RequestReplyException ex)
            {
                return Results.Json(new { error = $"Business Central could not run the credit check: {ex.ErrorText}" }, statusCode: StatusCodes.Status502BadGateway);
            }
            catch (NotSupportedException)
            {
                return Results.Json(new { error = "The credit check needs Service Bus; the API is running without it." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        // ---- Opportunities -------------------------------------------------------------------------

        api.MapGet("/opportunities", async (D365DbContext db, CancellationToken ct) =>
        {
            var users = await db.SystemUsers.AsNoTracking().ToDictionaryAsync(u => u.SystemUserId, u => u.FullName, ct);
            var accounts = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.AccountId, a => a.Name, ct);
            var groups = await db.ProductGroups.AsNoTracking().ToDictionaryAsync(g => g.ProductGroupId, g => g.Name, ct);
            var opportunities = await db.Opportunities.AsNoTracking().OrderBy(o => o.StateCode).ThenByDescending(o => o.ModifiedOn).ToListAsync(ct);
            return Results.Ok(opportunities.Select(o => new
            {
                opportunity = o,
                account = accounts.GetValueOrDefault(o.CustomerId),
                owner = users.GetValueOrDefault(o.OwnerId),
                productGroup = o.CsProductGroupId is Guid groupId ? groups.GetValueOrDefault(groupId) : null,
            }));
        });

        api.MapGet("/opportunities/{id:guid}", async (Guid id, D365DbContext db, CancellationToken ct) =>
        {
            var opportunity = await db.Opportunities.AsNoTracking().FirstOrDefaultAsync(o => o.OpportunityId == id, ct);
            if (opportunity is null) return Results.NotFound();

            var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.AccountId == opportunity.CustomerId, ct);
            var owner = await db.SystemUsers.AsNoTracking().FirstOrDefaultAsync(u => u.SystemUserId == opportunity.OwnerId, ct);
            var productGroup = opportunity.CsProductGroupId is Guid groupId
                ? await db.ProductGroups.AsNoTracking().FirstOrDefaultAsync(g => g.ProductGroupId == groupId, ct)
                : null;
            var quotes = await db.BcQuotes.AsNoTracking().Where(q => q.OpportunityId == id).OrderByDescending(q => q.LastSyncedOn).ToListAsync(ct);
            var timeline = await db.Timeline.AsNoTracking().Where(t => t.RegardingId == id).OrderByDescending(t => t.CreatedOn).ToListAsync(ct);
            return Results.Ok(new { opportunity, account, owner, productGroup, bcQuotes = quotes, timeline });
        });

        api.MapPut("/opportunities/{id:guid}", async (Guid id, OpportunityEdit edit, SalesService sales, CrmChangePublisher changes, CancellationToken ct) =>
        {
            var opportunity = await sales.UpdateOpportunityAsync(id, edit, ct);
            await changes.PublishOpportunityAsync(opportunity.OpportunityId, ct);
            return Results.Ok(opportunity);
        });
    }

    private static async ValueTask<object?> RuleFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (D365RuleException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
        }
    }
}

public sealed record UserBody(Guid UserId);
