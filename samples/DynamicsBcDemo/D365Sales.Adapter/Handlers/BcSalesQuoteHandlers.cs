using System.Globalization;
using D365Sales.Adapter.Clients;
using DynamicsBcDemo.Contracts.BusinessCentral;
using NimBus.SDK.EventHandlers;

namespace D365Sales.Adapter.Handlers;

/// <summary>A BC user created a quote linked to an opportunity: link it, show it read-only, and hand the account to Business Central.</summary>
public sealed class BcSalesQuoteCreatedHandler(IDataverseClient dataverse) : IEventHandler<BcSalesQuoteCreated>
{
    public Task Handle(BcSalesQuoteCreated message, IEventHandlerContext context, CancellationToken cancellationToken = default) =>
        QuoteMirror.ApplyAsync(dataverse, message, cancellationToken);
}

/// <summary>A Business Central quote changed (revised, sent, accepted): show its status on the deal.</summary>
public sealed class BcSalesQuoteUpdatedHandler(IDataverseClient dataverse) : IEventHandler<BcSalesQuoteUpdated>
{
    public Task Handle(BcSalesQuoteUpdated message, IEventHandlerContext context, CancellationToken cancellationToken = default) =>
        QuoteMirror.ApplyAsync(dataverse, message, cancellationToken);
}

/// <summary>
/// Mirrors a Business Central quote into Dynamics 365:
/// <list type="bullet">
/// <item>the read-only quote row and the opportunity's link and quote status — the opportunity itself
/// stays CRM's;</item>
/// <item>the account: from its first quote on, Business Central manages it, so CRM locks its master
/// data;</item>
/// <item>when the quote is accepted (it became an order in BC), the opportunity is closed as won with the
/// quote total. The order itself stays in Business Central.</item>
/// </list>
/// Every write is idempotent (PATCH/upsert, and winning a won opportunity changes nothing), so
/// redelivery and operator resubmits are harmless.
/// </summary>
internal static class QuoteMirror
{
    public const string Accepted = "Accepted";

    public static async Task ApplyAsync(IDataverseClient dataverse, BcSalesQuoteEvent quote, CancellationToken cancellationToken)
    {
        var mirror = new Dictionary<string, object?>
        {
            ["cs_quotenumber"] = quote.QuoteNumber,
            ["cs_status"] = quote.Status,
            ["cs_totalamount"] = quote.TotalAmountExcludingTax,
            ["cs_currency"] = quote.CurrencyCode,
            ["cs_validuntil"] = quote.ValidUntilDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["cs_senton"] = quote.SentDate,
            ["cs_acceptedon"] = quote.AcceptedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["cs_accountid@odata.bind"] = $"/accounts({quote.AccountId})",
        };
        if (quote.OpportunityId != Guid.Empty)
            mirror["cs_opportunityid@odata.bind"] = $"/opportunities({quote.OpportunityId})";
        await dataverse.UpsertBcQuoteAsync(quote.QuoteId, mirror, cancellationToken);

        if (quote.OpportunityId != Guid.Empty)
        {
            await dataverse.PatchOpportunityAsync(
                quote.OpportunityId,
                new Dictionary<string, object?>
                {
                    ["cs_bcquoteid"] = quote.QuoteId,
                    ["cs_bcquotenumber"] = quote.QuoteNumber,
                    ["cs_bcquotestatus"] = quote.Status,
                },
                cancellationToken);
        }

        var account = new Dictionary<string, object?>
        {
            ["cs_masterdataowner"] = MasterDataOwnerBusinessCentral,
        };
        // A prospect is quoted as a BC contact; keep its number so CRM can show where it lives.
        if (quote.SellToContactNumber is not null && quote.CustomerNumber is null)
            account["cs_bccontactnumber"] = quote.SellToContactNumber;
        await dataverse.PatchAccountAsync(quote.AccountId, account, cancellationToken);

        if (quote.Status == Accepted && quote.OpportunityId != Guid.Empty)
        {
            await dataverse.WinOpportunityAsync(
                quote.OpportunityId,
                quote.TotalAmountExcludingTax,
                quote.AcceptedDate ?? quote.DocumentDate,
                $"Won: Business Central quote {quote.QuoteNumber} accepted",
                cancellationToken);
        }
    }

    /// <summary>account.cs_masterdataowner: 2 = Business Central.</summary>
    private const int MasterDataOwnerBusinessCentral = 2;
}
