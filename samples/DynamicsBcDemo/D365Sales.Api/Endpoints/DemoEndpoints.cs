using D365Sales.Api.Data;
using D365Sales.Api.Domain;
using D365Sales.Api.Integration;
using DynamicsBcDemo.Contracts.Demo;

namespace D365Sales.Api.Endpoints;

/// <summary>
/// Demo-only: the pilot sales office requesting quotes all at once. A bounded, deterministic burst
/// is better on stage than a background simulator — it trips the circuit breaker the same way every
/// time, and it shows that one failing customer doesn't hold up the others.
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

    // Items a burst quotes; the discontinued CAM-HD is left out on purpose.
    private static readonly string[] BurstItems = ["CONN-WM8", "FORJ-2CH", "CBL-TOW100", "CAM-4K", "TSP-100"];

    public static void MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/demo/burst", async (BurstBody? body, D365DbContext db, SalesService sales, QuoteRequestPublisher quotes, TimeProvider clock, CancellationToken ct) =>
        {
            var count = Math.Clamp(body?.Count ?? 6, 1, MaxBurst);
            var sellers = SeedData.Sellers.Where(s => s.BcSalespersonCode is not null).ToList();
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
                    EstimatedCloseDate = run.UtcDateTime.Date.AddDays(30),
                    CloseProbability = 40,
                    StepName = SeedData.Stages.Develop,
                    OwnerId = seller.SystemUserId,
                    CsLinesRevision = 0,
                    CreatedOn = run,
                    ModifiedOn = run,
                };
                db.Accounts.Add(account);
                db.Opportunities.Add(opportunity);
                Timeline.Add(db, opportunity.OpportunityId, "Opportunity created", $"Pilot-office burst, seller {seller.FullName}.", Timeline.System, run);
                await db.SaveChangesAsync(ct);

                await sales.SetLinesAsync(
                    opportunity.OpportunityId,
                    [new OpportunityLineEdit(BurstItems[i % BurstItems.Length], 1 + (i % 3)), new OpportunityLineEdit("CONN-WM8", 4)],
                    ct);
                await quotes.RequestAsync(opportunity.OpportunityId, seller.SystemUserId, ct);

                created.Add(new { accountId = account.AccountId, opportunityId = opportunity.OpportunityId, name, seller = seller.FullName });
            }

            return Results.Ok(new { count, created });
        });
    }
}

/// <summary>Body of the burst: how many sellers request a quote at once.</summary>
public sealed record BurstBody(int? Count);
