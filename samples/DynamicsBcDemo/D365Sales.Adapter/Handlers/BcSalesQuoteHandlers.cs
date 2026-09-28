using System.Globalization;
using D365Sales.Adapter.Clients;
using DynamicsBcDemo.Contracts.BusinessCentral;
using DynamicsBcDemo.Contracts.Demo;
using NimBus.SDK.EventHandlers;

namespace D365Sales.Adapter.Handlers;

/// <summary>Business Central created a quote for an opportunity: link it and show it read-only.</summary>
public sealed class BcSalesQuoteCreatedHandler(IDataverseClient dataverse) : IEventHandler<BcSalesQuoteCreated>
{
    public Task Handle(BcSalesQuoteCreated message, IEventHandlerContext context, CancellationToken cancellationToken = default) =>
        QuoteMirror.ApplyAsync(dataverse, message, cancellationToken);
}

/// <summary>A Business Central quote changed (revised, sent, accepted): follow it in the pipeline.</summary>
public sealed class BcSalesQuoteUpdatedHandler(IDataverseClient dataverse) : IEventHandler<BcSalesQuoteUpdated>
{
    public Task Handle(BcSalesQuoteUpdated message, IEventHandlerContext context, CancellationToken cancellationToken = default) =>
        QuoteMirror.ApplyAsync(dataverse, message, cancellationToken);
}

/// <summary>
/// Mirrors a Business Central quote into Dynamics 365: the read-only quote row, the opportunity's
/// link, status and — the point for the sales manager — the estimated revenue, which from now on is
/// the real quote total instead of CRM's list-price estimate. Every write is idempotent (PATCH/upsert),
/// so redelivery and operator resubmits are harmless.
/// </summary>
internal static class QuoteMirror
{
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
            var opportunity = new Dictionary<string, object?>
            {
                ["cs_bcquoteid"] = quote.QuoteId,
                ["cs_bcquotenumber"] = quote.QuoteNumber,
                ["cs_bcquotestatus"] = quote.Status,
            };

            // An accepted quote is won by the order event, with the order amount; until then the
            // pipeline shows the quote total in the Propose stage.
            if (quote.Status is "Draft" or "Sent")
            {
                opportunity["estimatedvalue"] = quote.TotalAmountExcludingTax;
                opportunity["stepname"] = SeedData.Stages.Propose;
            }

            await dataverse.PatchOpportunityAsync(quote.OpportunityId, opportunity, cancellationToken);
        }

        // Business Central now knows the prospect as a contact, so later CRM edits to it are sent on.
        if (quote.SellToContactNumber is not null && quote.CustomerNumber is null)
        {
            await dataverse.PatchAccountAsync(
                quote.AccountId,
                new Dictionary<string, object?> { ["cs_bccontactnumber"] = quote.SellToContactNumber },
                cancellationToken);
        }
    }
}
