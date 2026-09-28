using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using D365Sales.Api.Data;
using D365Sales.Api.Domain;
using DynamicsBcDemo.Contracts.Demo;
using Microsoft.EntityFrameworkCore;

namespace D365Sales.Api.Endpoints;

/// <summary>
/// The surface the Dynamics 365 Sales adapter writes to, shaped like the Dataverse Web API
/// (<c>/api/data/v9.2</c>): PATCH by id or by alternate key, <c>@odata.bind</c> lookups, the
/// <c>WinOpportunity</c> action, and Dataverse-style errors. Pointing the adapter at a real
/// environment means adding an access token and the org URL.
/// These writes are the integration user's: they add timeline entries but are NEVER published back
/// to NimBus — in real Dataverse that is the plug-in step filtering out the integration user, and it
/// is what keeps BC → CRM updates from echoing back as CRM → BC events.
/// </summary>
public static partial class DataverseApiEndpoints
{
    public static void MapDataverseApiEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/data/v9.2").AddEndpointFilter(ErrorFilter);

        api.MapPatch("/accounts({id:guid})", async (Guid id, Dictionary<string, JsonElement> body, D365DbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.AccountId == id, ct)
                ?? throw DoesNotExist("account", id);
            ApplyAccount(db, account, body, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Upsert by alternate key: a Business Central customer CRM has never seen (created in BC).
        api.MapPatch("/accounts(cs_bccustomerid={bcCustomerId:guid})", async (Guid bcCustomerId, Dictionary<string, JsonElement> body, D365DbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.CsBcCustomerId == bcCustomerId, ct);
            if (account is null)
            {
                account = new Account
                {
                    AccountId = Guid.NewGuid(),
                    CsBcCustomerId = bcCustomerId,
                    OwnerId = SeedData.AlexRivera.SystemUserId,
                    CreatedOn = now,
                };
                db.Accounts.Add(account);
                Timeline.Add(db, account.AccountId, "Account created by Business Central", "An existing Business Central customer, synchronised into Dynamics 365.", Timeline.Integration, now);
            }

            ApplyAccount(db, account, body, now);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        api.MapPatch("/opportunities({id:guid})", async (Guid id, Dictionary<string, JsonElement> body, D365DbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var opportunity = await db.Opportunities.FirstOrDefaultAsync(o => o.OpportunityId == id, ct)
                ?? throw DoesNotExist("opportunity", id);
            ApplyOpportunity(opportunity, body, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Upsert of the read-only Business Central quote mirror (custom table cs_bcquote), keyed on BC's quote id.
        api.MapPatch("/cs_bcquotes(cs_bcquoteid={quoteId:guid})", async (Guid quoteId, Dictionary<string, JsonElement> body, D365DbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            var quote = await db.BcQuotes.FirstOrDefaultAsync(q => q.BcQuoteId == quoteId, ct);
            var created = quote is null;
            quote ??= new BcQuoteMirror { BcQuoteId = quoteId };
            var before = (quote.Status, quote.TotalAmount);

            ApplyQuote(quote, body, now);
            if (created) db.BcQuotes.Add(quote);

            if (quote.OpportunityId is Guid opportunityId)
            {
                var terms = $"{Timeline.Money(quote.TotalAmount, quote.CurrencyCode)}{(quote.ValidUntil is { } until ? ", valid until " + until.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : string.Empty)}";
                if (created)
                    Timeline.Add(db, opportunityId, $"Business Central created quote {quote.QuoteNumber}", $"{quote.Status}, {terms}.", Timeline.Integration, now);
                else if (before.Status != quote.Status)
                    Timeline.Add(db, opportunityId, $"Business Central quote {quote.QuoteNumber} is now {quote.Status}", $"{terms}.", Timeline.Integration, now);
                else if (before.TotalAmount != quote.TotalAmount)
                    Timeline.Add(db, opportunityId, $"Business Central quote {quote.QuoteNumber} was revised", $"{terms} (was {Timeline.Money(before.TotalAmount, quote.CurrencyCode)}).", Timeline.Integration, now);
            }

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Dataverse action: POST [org]/api/data/v9.2/WinOpportunity { Status: 3, OpportunityClose: {...} }.
        api.MapPost("/WinOpportunity", async (WinOpportunityBody body, D365DbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (body.Status != 3)
                throw new DataverseApiException(StatusCodes.Status400BadRequest, "0x80048d19", "WinOpportunity requires Status 3 (Won).");
            if (body.OpportunityClose is null || !body.OpportunityClose.TryGetValue("opportunityid@odata.bind", out var bind))
                throw new DataverseApiException(StatusCodes.Status400BadRequest, "0x80048d19", "OpportunityClose must bind opportunityid.");

            var opportunityId = BindId(bind);
            var opportunity = await db.Opportunities.FirstOrDefaultAsync(o => o.OpportunityId == opportunityId, ct)
                ?? throw DoesNotExist("opportunity", opportunityId);

            // Real Dataverse refuses to close a closed opportunity; the simulator accepts the repeat
            // so an operator resubmit of the order event stays harmless.
            if (opportunity.StateCode == OptionSets.State.WonOrQualified)
                return Results.NoContent();

            var now = clock.GetUtcNow();
            var close = body.OpportunityClose;
            opportunity.StateCode = OptionSets.State.WonOrQualified;
            opportunity.StepName = SeedData.Stages.Close;
            opportunity.CloseProbability = 100;
            opportunity.ActualValue = close.TryGetValue("actualrevenue", out var revenue) ? Dec(revenue) : opportunity.EstimatedValue;
            opportunity.ActualCloseDate = close.TryGetValue("actualend", out var end) ? Date(end) : now.UtcDateTime.Date;
            opportunity.ModifiedOn = now;
            var subject = close.TryGetValue("subject", out var s) ? Str(s) : null;
            Timeline.Add(db, opportunity.OpportunityId, "Opportunity won", $"{subject ?? "Won"}. Actual revenue {Timeline.Money(opportunity.ActualValue ?? 0m)}.", Timeline.Integration, now);

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static void ApplyAccount(D365DbContext db, Account account, Dictionary<string, JsonElement> body, DateTimeOffset now)
    {
        var wasCustomer = account.CustomerTypeCode == OptionSets.RelationshipType.Customer;
        var creditBefore = (account.CreditLimit, account.CsBcBalanceDue, account.CsBcBlocked);

        foreach (var (column, value) in body)
        {
            switch (column)
            {
                case "name": account.Name = Str(value) ?? account.Name; break;
                case "accountnumber": account.AccountNumber = Str(value); break;
                case "customertypecode": account.CustomerTypeCode = value.GetInt32(); break;
                case "creditlimit": account.CreditLimit = Dec(value); break;
                case "creditonhold": account.CreditOnHold = value.GetBoolean(); break;
                case "address1_line1": account.Address1Line1 = Str(value); break;
                case "address1_city": account.Address1City = Str(value); break;
                case "address1_postalcode": account.Address1PostalCode = Str(value); break;
                case "address1_country": account.Address1Country = Str(value); break;
                case "telephone1": account.Telephone1 = Str(value); break;
                case "websiteurl": account.WebsiteUrl = Str(value); break;
                case "cs_vatnumber": account.CsVatNumber = Str(value); break;
                case "cs_bccustomerid": account.CsBcCustomerId = value.ValueKind == JsonValueKind.Null ? null : value.GetGuid(); break;
                case "cs_bccontactnumber": account.CsBcContactNumber = Str(value); break;
                case "cs_bcblocked": account.CsBcBlocked = Str(value); break;
                case "cs_bcbalancedue": account.CsBcBalanceDue = Dec(value); break;
                case "cs_bcpaymentterms": account.CsBcPaymentTerms = Str(value); break;
                case "cs_masterdataowner": account.CsMasterDataOwner = value.GetInt32(); break;
                case "cs_bclastsyncedon": account.CsBcLastSyncedOn = value.ValueKind == JsonValueKind.Null ? null : value.GetDateTimeOffset(); break;
                case "cs_bcsalespersoncode":
                    // Resolve the owning seller for accounts Business Central introduces.
                    if (Str(value) is { } code && SeedData.Sellers.FirstOrDefault(x => x.BcSalespersonCode == code) is { } seller)
                        account.OwnerId = seller.SystemUserId;
                    break;
                default: throw UnknownProperty(column, "account");
            }
        }

        account.ModifiedOn = now;

        if (!wasCustomer && account.CustomerTypeCode == OptionSets.RelationshipType.Customer)
        {
            Timeline.Add(
                db,
                account.AccountId,
                $"Became a Business Central customer ({account.AccountNumber})",
                "Business Central now owns this account's master data; the fields are read-only here.",
                Timeline.Integration,
                now);
        }
        else if (wasCustomer && creditBefore != (account.CreditLimit, account.CsBcBalanceDue, account.CsBcBlocked))
        {
            var blocked = string.IsNullOrWhiteSpace(account.CsBcBlocked) ? "not blocked" : $"blocked: {account.CsBcBlocked}";
            Timeline.Add(
                db,
                account.AccountId,
                "Credit data updated from Business Central",
                $"Credit limit {Timeline.Money(account.CreditLimit ?? 0m)}, balance {Timeline.Money(account.CsBcBalanceDue ?? 0m)}, {blocked}.",
                Timeline.Integration,
                now);
        }
    }

    private static void ApplyOpportunity(Opportunity opportunity, Dictionary<string, JsonElement> body, DateTimeOffset now)
    {
        foreach (var (column, value) in body)
        {
            switch (column)
            {
                case "estimatedvalue": opportunity.EstimatedValue = Dec(value); break;
                case "stepname": opportunity.StepName = Str(value) ?? opportunity.StepName; break;
                case "closeprobability": opportunity.CloseProbability = value.GetInt32(); break;
                case "cs_bcquoteid": opportunity.CsBcQuoteId = value.ValueKind == JsonValueKind.Null ? null : value.GetGuid(); break;
                case "cs_bcquotenumber": opportunity.CsBcQuoteNumber = Str(value); break;
                case "cs_bcquotestatus": opportunity.CsBcQuoteStatus = Str(value); break;
                case "cs_bcordernumber": opportunity.CsBcOrderNumber = Str(value); break;
                default: throw UnknownProperty(column, "opportunity");
            }
        }

        opportunity.ModifiedOn = now;
    }

    private static void ApplyQuote(BcQuoteMirror quote, Dictionary<string, JsonElement> body, DateTimeOffset now)
    {
        foreach (var (column, value) in body)
        {
            switch (column)
            {
                case "cs_quotenumber": quote.QuoteNumber = Str(value) ?? quote.QuoteNumber; break;
                case "cs_status": quote.Status = Str(value) ?? quote.Status; break;
                case "cs_totalamount": quote.TotalAmount = Dec(value) ?? 0m; break;
                case "cs_currency": quote.CurrencyCode = Str(value) ?? quote.CurrencyCode; break;
                case "cs_validuntil": quote.ValidUntil = Date(value); break;
                case "cs_senton": quote.SentOn = value.ValueKind == JsonValueKind.Null ? null : value.GetDateTimeOffset(); break;
                case "cs_acceptedon": quote.AcceptedOn = Date(value); break;
                case "cs_opportunityid@odata.bind": quote.OpportunityId = BindId(value); break;
                case "cs_accountid@odata.bind": quote.AccountId = BindId(value); break;
                default: throw UnknownProperty(column, "cs_bcquote");
            }
        }

        quote.LastSyncedOn = now;
    }

    private static string? Str(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetString();

    private static decimal? Dec(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetDecimal();

    private static DateTime? Date(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null
            ? null
            : DateTime.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal).Date;

    /// <summary>Reads the id from an <c>@odata.bind</c> value such as <c>/opportunities(3f2b...)</c>.</summary>
    private static Guid BindId(JsonElement value)
    {
        var match = BindPattern().Match(value.GetString() ?? string.Empty);
        return match.Success
            ? Guid.Parse(match.Groups["id"].Value)
            : throw new DataverseApiException(StatusCodes.Status400BadRequest, "0x80048d19", $"'{value}' is not a valid @odata.bind reference.");
    }

    [GeneratedRegex(@"\((?<id>[0-9a-fA-F-]{36})\)\s*$")]
    private static partial Regex BindPattern();

    private static DataverseApiException DoesNotExist(string entity, Guid id) =>
        new(StatusCodes.Status404NotFound, "0x80040217", $"{entity} With Ids = {id} Do Not Exist");

    private static DataverseApiException UnknownProperty(string column, string entity) =>
        new(StatusCodes.Status400BadRequest, "0x80060888", $"The property '{column}' does not exist on type 'Microsoft.Dynamics.CRM.{entity}'.");

    private static async ValueTask<object?> ErrorFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (DataverseApiException ex)
        {
            return Results.Json(new { error = new { code = ex.Code, message = ex.Message } }, statusCode: ex.StatusCode);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return Results.Json(new { error = new { code = "0x80048d19", message = $"Error identified in Payload: {ex.Message}" } }, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}

/// <summary>Body of the WinOpportunity action.</summary>
public sealed record WinOpportunityBody(int Status, Dictionary<string, JsonElement>? OpportunityClose);
