using D365Sales.Api.Data;
using D365Sales.Api.Domain;
using D365Sales.Api.Integration;
using DynamicsBcDemo.Contracts.Demo;
using Microsoft.EntityFrameworkCore;

namespace D365Sales.Api.Endpoints;

/// <summary>
/// Demo-only controls, used by the presenter cockpit:
/// <list type="bullet">
/// <item>the pilot sales office creating new prospects and opportunities all at once — a bounded,
/// deterministic burst is better on stage than a background simulator: it trips the circuit breaker
/// the same way every time, and it shows that one failing customer doesn't hold up the others;</item>
/// <item>delivering the last opportunity change again, the way a source system's retry can.</item>
/// </list>
/// </summary>
public static class DemoEndpoints
{
    private const int MaxBurst = 24;

    private static readonly string[] NamePrefixes =
        ["Aurora", "Kittiwake", "Northlight", "Seabright", "Tidewater", "Blue Fjord", "Deepwater", "Harbourline", "Skerry", "Coralline", "Driftwood", "Longshore"];

    private static readonly string[] NameSuffixes =
        ["Marine Survey", "Offshore Services", "Ocean Robotics", "Hydrographics", "Subsea Works", "Cable Laying"];

    private static readonly (string City, string Country)[] Places =
        [("Bergen", "NO"), ("Rotterdam", "NL"), ("Brest", "FR"), ("Cork", "IE"), ("Kiel", "DE"), ("Gdansk", "PL"), ("Houston", "US"), ("Perth", "AU")];

    private static readonly decimal[] EstimatedValues = [48000m, 125000m, 26500m, 212000m, 74000m, 96500m];

    public static void MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/demo/burst", async (BurstBody? body, D365DbContext db, SalesService sales, CrmChangePublisher changes, TimeProvider clock, CancellationToken ct) =>
        {
            var count = Math.Clamp(body?.Count ?? 6, 1, MaxBurst);
            var sellers = SeedData.Sellers.Where(s => s.BcSalespersonCode is not null).ToList();
            var groups = await db.ProductGroups.AsNoTracking().OrderBy(g => g.CsCode).Select(g => g.ProductGroupId).ToListAsync(ct);
            var run = clock.GetUtcNow();
            var created = new List<object>();

            for (var i = 0; i < count; i++)
            {
                var seller = sellers[i % sellers.Count];
                var (city, country) = Places[i % Places.Length];
                var name = $"{NamePrefixes[(i + run.Second) % NamePrefixes.Length]} {NameSuffixes[(i + run.Minute) % NameSuffixes.Length]} {run:HHmm}-{i + 1:00}";

                var account = new Account
                {
                    AccountId = Guid.NewGuid(),
                    Name = name,
                    CustomerTypeCode = OptionSets.RelationshipType.Prospect,
                    Address1City = city,
                    Address1Country = country,
                    OwnerId = seller.SystemUserId,
                    CsMasterDataOwner = OptionSets.MasterDataOwner.Dynamics365,
                    Description = "Created by the pilot-office burst (demo).",
                    CreatedOn = run,
                    ModifiedOn = run,
                };
                var opportunity = new Opportunity
                {
                    OpportunityId = Guid.NewGuid(),
                    CsNumber = await sales.NextOpportunityNumberAsync(ct),
                    Name = $"Equipment for {name}",
                    CustomerId = account.AccountId,
                    EstimatedValue = EstimatedValues[i % EstimatedValues.Length],
                    EstimatedCloseDate = run.UtcDateTime.Date.AddDays(30),
                    CloseProbability = 40,
                    StepName = SeedData.Stages.Develop,
                    CsProductGroupId = groups.Count == 0 ? null : groups[i % groups.Count],
                    OwnerId = seller.SystemUserId,
                    CreatedOn = run,
                    ModifiedOn = run,
                };
                db.Accounts.Add(account);
                db.Opportunities.Add(opportunity);
                Timeline.Add(db, opportunity.OpportunityId, "Opportunity created", $"Pilot-office burst, seller {seller.FullName}. Sent to Business Central.", Timeline.System, run);
                await db.SaveChangesAsync(ct);

                await changes.PublishProspectAsync(account.AccountId, ct);
                await changes.PublishOpportunityAsync(opportunity.OpportunityId, ct);

                created.Add(new { accountId = account.AccountId, opportunityId = opportunity.OpportunityId, number = opportunity.CsNumber, name, seller = seller.FullName });
            }

            return Results.Ok(new { count, created });
        });

        app.MapPost("/api/demo/redeliver", async (CrmChangePublisher changes, CancellationToken ct) =>
            await changes.RedeliverLastOpportunityChangeAsync(ct) is { } change
                ? Results.Ok(new { messageId = change.MessageId, summary = change.Summary })
                : Results.NotFound(new { error = "No opportunity change has been sent since the start. Change an opportunity first." }));
    }
}

/// <summary>Body of the burst: how many sellers create a prospect and an opportunity at once.</summary>
public sealed record BurstBody(int? Count);
