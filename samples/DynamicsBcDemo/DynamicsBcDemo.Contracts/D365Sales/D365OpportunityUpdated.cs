using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace DynamicsBcDemo.Contracts.D365Sales;

[Description("Published by Dynamics 365 Sales when an opportunity is created or a seller changes it. Business Central keeps a copy of every CRM opportunity, so a BC user can create the quote in Business Central and link it to the right opportunity. CRM owns the opportunity; Business Central only reads it.")]
[SessionKey(nameof(AccountId))]
public class D365OpportunityUpdated : Event
{
    /// <summary>Opportunity states (Dataverse <c>statecode</c>) as text.</summary>
    public static class Statuses
    {
        public const string Open = "Open";
        public const string Won = "Won";
        public const string Lost = "Lost";
    }

    public static readonly D365OpportunityUpdated Example = new()
    {
        AccountId = Guid.Parse("acc00000-0000-4000-8000-000000000105"),
        OpportunityId = Guid.Parse("0bb00000-0000-4000-8000-000000000105"),
        OpportunityNumber = "OPP-10025",
        Name = "ROV winch upgrade for research vessel",
        AccountName = "Tailspin Marine Research",
        SellerEmail = "alex.rivera@contososubsea.example",
        SellerName = "Alex Rivera",
        EstimatedValue = 210000m,
        CurrencyCode = "EUR",
        EstimatedCloseDate = new DateTime(2026, 11, 27),
        ProductGroupCode = "WINCH",
        Status = Statuses.Open,
        UpdatedAt = new DateTimeOffset(2026, 9, 28, 9, 35, 0, TimeSpan.Zero),
    };

    [Required]
    [Description("The CRM account (customerid). Session key: every message about this account stays in order.")]
    public Guid AccountId { get; set; }

    [Required]
    [Description("opportunityid")]
    public Guid OpportunityId { get; set; }

    [Required]
    [Description("The opportunity number (cs_number), e.g. OPP-10025. BC shows it as the quote's external document number.")]
    public string OpportunityNumber { get; set; } = string.Empty;

    [Required]
    [Description("The opportunity topic.")]
    public string Name { get; set; } = string.Empty;

    [Required]
    [Description("The account's name, for BC's list of CRM opportunities.")]
    public string AccountName { get; set; } = string.Empty;

    [Description("The BC customer id when the account is already a buying customer; null for a prospect.")]
    public Guid? BcCustomerId { get; set; }

    // Deliberately not [Sensitive]: it is the seller's work address, which BC needs to match a
    // salesperson and which operators must see to fix a missing one.
    [Required]
    [Description("The owning seller's e-mail. BC resolves it to a salesperson.")]
    public string SellerEmail { get; set; } = string.Empty;

    [Description("The owning seller's name.")]
    public string? SellerName { get; set; }

    [Description("Estimated revenue.")]
    public decimal? EstimatedValue { get; set; }

    [Required]
    [Description("ISO 4217 currency code.")]
    public string CurrencyCode { get; set; } = "EUR";

    [Description("Estimated close date.")]
    public DateTime? EstimatedCloseDate { get; set; }

    [Description("The product group, as the BC item category code it came from. Null until the seller picks one.")]
    public string? ProductGroupCode { get; set; }

    [Required]
    [Description("Open, Won or Lost.")]
    public string Status { get; set; } = Statuses.Open;

    [Description("When the opportunity was created or changed in CRM.")]
    public DateTimeOffset UpdatedAt { get; set; }
}
