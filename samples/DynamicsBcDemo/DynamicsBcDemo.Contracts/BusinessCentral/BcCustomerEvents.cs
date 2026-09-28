using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;
using NimBus.Core.Messages.PII;

namespace DynamicsBcDemo.Contracts.BusinessCentral;

/// <summary>How a Business Central customer came to exist.</summary>
public enum BcCustomerOrigin
{
    /// <summary>Created directly in Business Central (for example an existing customer loaded at go-live).</summary>
    CreatedInBc = 0,

    /// <summary>Converted from a CRM prospect contact when its quote became an order.</summary>
    ConvertedFromProspect = 1,
}

/// <summary>
/// Shared shape of the Business Central customer events. Field names follow the BC API v2.0
/// <c>customer</c> resource. BC owns this data; CRM only mirrors it.
/// </summary>
[SessionKey(nameof(AccountId))]
public abstract class BcCustomerEvent : Event
{
    [Required]
    [Description("Session key. The CRM account id when BC knows it; otherwise the BC customer id, so the key is still stable per customer.")]
    public Guid AccountId { get; set; }

    [Description("The CRM account id (CRM reference field, AL extension). Null for customers created directly in BC.")]
    public Guid? CrmAccountId { get; set; }

    [Required]
    [Description("customer.id")]
    public Guid CustomerId { get; set; }

    [Required]
    [Description("customer.number, e.g. C00050.")]
    public string CustomerNumber { get; set; } = string.Empty;

    [Required]
    [Description("customer.displayName.")]
    public string DisplayName { get; set; } = string.Empty;

    [Description("customer.addressLine1.")]
    public string? AddressLine1 { get; set; }

    [Description("customer.city.")]
    public string? City { get; set; }

    [Description("customer.postalCode.")]
    public string? PostalCode { get; set; }

    [Required]
    [Description("customer.country (ISO 3166-1 alpha-2).")]
    public string CountryCode { get; set; } = string.Empty;

    [Description("customer.phoneNumber.")]
    public string? PhoneNumber { get; set; }

    [Description("customer.website.")]
    public string? Website { get; set; }

    [Sensitive(Mode = MaskMode.PartialReveal, Reveal = 4)]
    [Description("customer.taxRegistrationNumber.")]
    public string? TaxRegistrationNumber { get; set; }

    [Description("customer.salespersonCode.")]
    public string? SalespersonCode { get; set; }

    [Description("customer.creditLimit.")]
    public decimal CreditLimit { get; set; }

    [Description("customer.balanceDue.")]
    public decimal BalanceDue { get; set; }

    [Description("customer.blocked: empty, Ship, Invoice or All.")]
    public string Blocked { get; set; } = string.Empty;

    [Description("Payment terms code, e.g. 30 DAYS.")]
    public string? PaymentTermsCode { get; set; }

    [Description("customer.currencyCode.")]
    public string CurrencyCode { get; set; } = "EUR";

    [Description("How the customer came to exist in BC.")]
    public BcCustomerOrigin Origin { get; set; }

    [Description("customer.lastModifiedDateTime.")]
    public DateTimeOffset ChangedAt { get; set; }
}

[Description("Published by Business Central when a customer is created — normally when a CRM prospect's quote becomes an order and the prospect contact becomes a buying customer. CRM flips the account from Prospect to Customer and shows the BC customer data read-only.")]
public class BcCustomerCreated : BcCustomerEvent
{
    public static readonly BcCustomerCreated Example = new()
    {
        AccountId = Guid.Parse("acc00000-0000-4000-8000-000000000105"),
        CrmAccountId = Guid.Parse("acc00000-0000-4000-8000-000000000105"),
        CustomerId = Guid.Parse("bc000000-0000-4000-8000-0000000c0050"),
        CustomerNumber = "C00050",
        DisplayName = "Tailspin Marine Research",
        AddressLine1 = "Ocean Way 12",
        City = "Southampton",
        PostalCode = "SO14 3ZH",
        CountryCode = "GB",
        TaxRegistrationNumber = "GB123456789",
        SalespersonCode = "AR",
        CreditLimit = 100000m,
        BalanceDue = 0m,
        Blocked = string.Empty,
        PaymentTermsCode = "30 DAYS",
        CurrencyCode = "EUR",
        Origin = BcCustomerOrigin.ConvertedFromProspect,
        ChangedAt = new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero),
    };
}

[Description("Published by Business Central when customer master data changes (address, payment terms, credit limit, balance or blocked status) and for every customer in the initial sync at go-live. CRM creates or updates its read-only copy of the account.")]
public class BcCustomerUpdated : BcCustomerEvent
{
    public static readonly BcCustomerUpdated Example = new()
    {
        AccountId = Guid.Parse("acc00000-0000-4000-8000-000000000101"),
        CrmAccountId = Guid.Parse("acc00000-0000-4000-8000-000000000101"),
        CustomerId = Guid.Parse("bc000000-0000-4000-8000-0000000c0010"),
        CustomerNumber = "C00010",
        DisplayName = "Fabrikam Offshore Energy",
        City = "Stavanger",
        CountryCode = "NO",
        SalespersonCode = "AR",
        CreditLimit = 300000m,
        BalanceDue = 42350m,
        Blocked = "Ship",
        PaymentTermsCode = "30 DAYS",
        CurrencyCode = "EUR",
        Origin = BcCustomerOrigin.CreatedInBc,
        ChangedAt = new DateTimeOffset(2026, 9, 28, 10, 30, 0, TimeSpan.Zero),
    };
}
