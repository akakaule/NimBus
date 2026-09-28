using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace DynamicsBcDemo.Contracts.D365Sales;

[Description("Command: create a sales quote in Business Central for a Dynamics 365 Sales opportunity. Quotes are made in BC, so CRM asks rather than tells. Exactly one consumer (BusinessCentralEndpoint). For a prospect, BC quotes a prospect contact and only creates the customer when the quote becomes an order; for an existing customer, BC quotes the customer directly. Session-keyed on the CRM account, so it is ordered with every other message about that customer.")]
[SessionKey(nameof(AccountId))]
public class CreateBcSalesQuote : Command
{
    public static readonly CreateBcSalesQuote Example = new()
    {
        AccountId = Guid.Parse("acc00000-0000-4000-8000-000000000105"),
        OpportunityId = Guid.Parse("0bb00000-0000-4000-8000-000000000105"),
        OpportunityNumber = "OPP-10025",
        OpportunityName = "ROV winch upgrade for research vessel",
        Prospect = new ProspectDetails
        {
            Name = "Tailspin Marine Research",
            VatRegistrationNumber = "GB123456789",
            AddressLine1 = "Ocean Way 12",
            City = "Southampton",
            PostalCode = "SO14 3ZH",
            CountryCode = "GB",
            Phone = "+44 23 8000 1000",
            Website = "https://tailspin-marine.example",
        },
        PrimaryContact = new ContactPersonDetails
        {
            FullName = "Hannah Okafor",
            Email = "hannah.okafor@tailspin-marine.example",
            Phone = "+44 23 8000 1001",
        },
        SellerEmail = "alex.rivera@contososubsea.example",
        SellerName = "Alex Rivera",
        CurrencyCode = "EUR",
        Revision = 1,
        Lines =
        [
            new QuoteRequestLine { ItemNumber = "WNCH-E20", Description = "Electric ROV winch, 20 kN", Quantity = 1 },
            new QuoteRequestLine { ItemNumber = "CBL-TOW100", Description = "Armoured tow cable, per 100 m", Quantity = 3 },
        ],
        RequestedAt = new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero),
    };

    [Required]
    [Description("The CRM account (accountid). Session key: all messages about one customer are processed in order.")]
    public Guid AccountId { get; set; }

    [Required]
    [Description("The CRM opportunity the quote is for. BC stores it in a CRM reference field (AL extension).")]
    public Guid OpportunityId { get; set; }

    [Required]
    [Description("The opportunity number. BC puts it in the quote's External Document No.")]
    public string OpportunityNumber { get; set; } = string.Empty;

    [Required]
    [Description("The opportunity topic, used as the quote's description in BC.")]
    public string OpportunityName { get; set; } = string.Empty;

    [Description("The BC customer number when the account is already a buying customer; null for a prospect.")]
    public string? BcCustomerNumber { get; set; }

    [Required]
    [Description("The prospect's master data as CRM knows it. BC creates or updates a prospect contact from it.")]
    public ProspectDetails Prospect { get; set; } = new();

    [Description("The primary contact person at the prospect.")]
    public ContactPersonDetails? PrimaryContact { get; set; }

    [Required]
    [Description("Work e-mail of the seller who owns the opportunity. BC resolves the salesperson code from it; not masked because operators need it to fix a missing salesperson.")]
    public string SellerEmail { get; set; } = string.Empty;

    [Description("Display name of the seller.")]
    public string? SellerName { get; set; }

    [Required]
    [Description("ISO 4217 currency code for the quote.")]
    public string CurrencyCode { get; set; } = "EUR";

    [Description("Quote-request revision of the opportunity. It is part of the deterministic MessageId, so a double-clicked request is detected as a duplicate while a changed request is not.")]
    public int Revision { get; set; }

    [Required]
    [MinLength(1)]
    [Description("The product lines to quote. BC prices them from its item card.")]
    public List<QuoteRequestLine> Lines { get; set; } = [];

    [Description("When the seller requested the quote (CRM clock).")]
    public DateTimeOffset RequestedAt { get; set; }
}
