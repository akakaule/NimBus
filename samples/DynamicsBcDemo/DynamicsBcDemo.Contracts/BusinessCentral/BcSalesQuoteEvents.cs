using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace DynamicsBcDemo.Contracts.BusinessCentral;

/// <summary>
/// Shared shape of the Business Central sales-quote events. Field names follow the BC API v2.0
/// <c>salesQuote</c> resource where one exists.
/// </summary>
[SessionKey(nameof(AccountId))]
public abstract class BcSalesQuoteEvent : Event
{
    [Required]
    [Description("The CRM account the quote belongs to. Session key.")]
    public Guid AccountId { get; set; }

    [Required]
    [Description("salesQuote.id")]
    public Guid QuoteId { get; set; }

    [Required]
    [Description("salesQuote.number, e.g. S-QUO1002.")]
    public string QuoteNumber { get; set; } = string.Empty;

    [Required]
    [Description("The CRM opportunity the quote was requested for (CRM reference field, AL extension).")]
    public Guid OpportunityId { get; set; }

    [Description("salesQuote.externalDocumentNumber: the CRM opportunity number.")]
    public string? ExternalDocumentNumber { get; set; }

    [Required]
    [Description("salesQuote.status: Draft, Sent, Accepted or Expired.")]
    public string Status { get; set; } = string.Empty;

    [Description("The BC customer number, once the quote is for a buying customer; null while it is for a prospect contact.")]
    public string? CustomerNumber { get; set; }

    [Description("The BC prospect contact number while the quote is for a prospect.")]
    public string? SellToContactNumber { get; set; }

    [Description("Sell-to name on the quote.")]
    public string? SellToName { get; set; }

    [Description("salesQuote.salesperson: the BC salesperson code.")]
    public string? SalespersonCode { get; set; }

    [Required]
    [Description("salesQuote.currencyCode.")]
    public string CurrencyCode { get; set; } = "EUR";

    [Description("salesQuote.totalAmountExcludingTax.")]
    public decimal TotalAmountExcludingTax { get; set; }

    [Description("Number of quote lines.")]
    public int LineCount { get; set; }

    [Description("salesQuote.documentDate.")]
    public DateTime DocumentDate { get; set; }

    [Description("salesQuote.validUntilDate.")]
    public DateTime? ValidUntilDate { get; set; }

    [Description("salesQuote.sentDate.")]
    public DateTimeOffset? SentDate { get; set; }

    [Description("salesQuote.acceptedDate.")]
    public DateTime? AcceptedDate { get; set; }

    [Description("salesQuote.lastModifiedDateTime.")]
    public DateTimeOffset ChangedAt { get; set; }
}

[Description("Published by Business Central when it creates a sales quote for a CRM opportunity. CRM links the quote to the opportunity and shows it read-only.")]
public class BcSalesQuoteCreated : BcSalesQuoteEvent
{
    public static readonly BcSalesQuoteCreated Example = new()
    {
        AccountId = Guid.Parse("acc00000-0000-4000-8000-000000000105"),
        QuoteId = Guid.Parse("b0e00000-0000-4000-8000-000000001002"),
        QuoteNumber = "S-QUO1002",
        OpportunityId = Guid.Parse("0bb00000-0000-4000-8000-000000000105"),
        ExternalDocumentNumber = "OPP-10025",
        Status = "Draft",
        SellToContactNumber = "CT000101",
        SellToName = "Tailspin Marine Research",
        SalespersonCode = "AR",
        CurrencyCode = "EUR",
        TotalAmountExcludingTax = 212800m,
        LineCount = 2,
        DocumentDate = new DateTime(2026, 9, 28),
        ValidUntilDate = new DateTime(2026, 10, 28),
        ChangedAt = new DateTimeOffset(2026, 9, 28, 9, 0, 5, TimeSpan.Zero),
    };
}

[Description("Published by Business Central when a sales quote changes: prices or lines edited, sent to the customer, or accepted when it becomes an order. CRM updates the opportunity's estimated revenue and quote status from it.")]
public class BcSalesQuoteUpdated : BcSalesQuoteEvent
{
    public static readonly BcSalesQuoteUpdated Example = new()
    {
        AccountId = Guid.Parse("acc00000-0000-4000-8000-000000000105"),
        QuoteId = Guid.Parse("b0e00000-0000-4000-8000-000000001002"),
        QuoteNumber = "S-QUO1002",
        OpportunityId = Guid.Parse("0bb00000-0000-4000-8000-000000000105"),
        ExternalDocumentNumber = "OPP-10025",
        Status = "Sent",
        SellToContactNumber = "CT000101",
        SellToName = "Tailspin Marine Research",
        SalespersonCode = "AR",
        CurrencyCode = "EUR",
        TotalAmountExcludingTax = 204600m,
        LineCount = 2,
        DocumentDate = new DateTime(2026, 9, 28),
        ValidUntilDate = new DateTime(2026, 10, 28),
        SentDate = new DateTimeOffset(2026, 9, 28, 9, 20, 0, TimeSpan.Zero),
        ChangedAt = new DateTimeOffset(2026, 9, 28, 9, 20, 0, TimeSpan.Zero),
    };
}
