namespace BusinessCentral.Api.Data;

// A deliberately small slice of the Business Central data model, named after the BC API v2.0
// resources (customer, salesQuote, salesOrder, item). Fields marked "AL extension" don't exist in
// standard BC: a real implementation adds them with a small AL table extension.

/// <summary>Salesperson/Purchaser. CRM sellers map to it by e-mail.</summary>
public class Salesperson
{
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Item card. BC is the master of items and prices.</summary>
public class Item
{
    public Guid Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public string BaseUnitOfMeasure { get; set; } = "PCS";
    public bool Blocked { get; set; }
}

/// <summary>
/// A company contact that is not (yet) a customer: the prospect CRM asked a quote for. BC quotes
/// the contact, and only creates a customer from it when the quote becomes an order.
/// </summary>
public class Contact
{
    public Guid Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public string Type { get; set; } = "Company";
    public string DisplayName { get; set; } = string.Empty;
    public string? AddressLine1 { get; set; }
    public string? City { get; set; }
    public string? PostalCode { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? Website { get; set; }
    public string? VatRegistrationNumber { get; set; }
    public string? ContactPersonName { get; set; }
    public string? ContactPersonEmail { get; set; }
    public string? ContactPersonPhone { get; set; }
    public string CustomerTemplateCode { get; set; } = string.Empty;

    /// <summary>AL extension: the CRM account this prospect came from.</summary>
    public Guid? CrmAccountId { get; set; }

    /// <summary>Set once the contact has been converted to a customer (Make Order).</summary>
    public Guid? CustomerId { get; set; }
    public string? CustomerNumber { get; set; }

    public DateTimeOffset LastModifiedDateTime { get; set; }
}

/// <summary>A buying customer. BC owns this record.</summary>
public class Customer
{
    public Guid Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Type { get; set; } = "Company";
    public string? AddressLine1 { get; set; }
    public string? City { get; set; }
    public string? PostalCode { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }
    public string? TaxRegistrationNumber { get; set; }
    public string? SalespersonCode { get; set; }
    public decimal CreditLimit { get; set; }
    public decimal BalanceDue { get; set; }
    public decimal OverdueAmount { get; set; }

    /// <summary>" " (empty), "Ship", "Invoice" or "All" — stored as empty string for none.</summary>
    public string Blocked { get; set; } = string.Empty;

    public string? PaymentTermsCode { get; set; }
    public string CurrencyCode { get; set; } = "EUR";

    /// <summary>AL extension: the CRM account the customer is linked to.</summary>
    public Guid? CrmAccountId { get; set; }

    /// <summary><see cref="DynamicsBcDemo.Contracts.BusinessCentral.BcCustomerOrigin"/> as an int.</summary>
    public int Origin { get; set; }

    public DateTimeOffset LastModifiedDateTime { get; set; }
}

/// <summary>Sales quote header.</summary>
public class SalesQuote
{
    public Guid Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public string? ExternalDocumentNumber { get; set; }
    public string? Description { get; set; }
    public DateTime DocumentDate { get; set; }
    public DateTime? ValidUntilDate { get; set; }
    public Guid? CustomerId { get; set; }
    public string? CustomerNumber { get; set; }
    public Guid? SellToContactId { get; set; }
    public string? SellToContactNumber { get; set; }
    public string SellToName { get; set; } = string.Empty;
    public string? SalespersonCode { get; set; }

    /// <summary>Draft, Sent, Accepted or Expired (salesQuote.status).</summary>
    public string Status { get; set; } = QuoteStatus.Draft;

    public DateTimeOffset? SentDate { get; set; }
    public DateTime? AcceptedDate { get; set; }
    public string CurrencyCode { get; set; } = "EUR";
    public decimal TotalAmountExcludingTax { get; set; }

    /// <summary>AL extension: the CRM opportunity and account the quote was requested for.</summary>
    public Guid? CrmOpportunityId { get; set; }
    public Guid? CrmAccountId { get; set; }

    /// <summary>AL extension: the CRM quote-request revision last applied to this quote.</summary>
    public int CrmRequestRevision { get; set; }

    /// <summary>The order the quote became (Make Order).</summary>
    public string? OrderNumber { get; set; }

    public DateTimeOffset LastModifiedDateTime { get; set; }
    public List<SalesQuoteLine> Lines { get; set; } = [];
}

/// <summary>Sales quote line.</summary>
public class SalesQuoteLine
{
    public Guid Id { get; set; }
    public Guid QuoteId { get; set; }
    public int Sequence { get; set; }
    public string ItemNumber { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal AmountExcludingTax { get; set; }
}

/// <summary>Sales order header.</summary>
public class SalesOrder
{
    public Guid Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public string? ExternalDocumentNumber { get; set; }
    public string? QuoteNumber { get; set; }
    public DateTime OrderDate { get; set; }
    public Guid CustomerId { get; set; }
    public string CustomerNumber { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string? SalespersonCode { get; set; }
    public string Status { get; set; } = "Open";
    public string CurrencyCode { get; set; } = "EUR";
    public decimal TotalAmountExcludingTax { get; set; }
    public Guid? CrmOpportunityId { get; set; }
    public Guid? CrmAccountId { get; set; }
    public DateTimeOffset LastModifiedDateTime { get; set; }
    public List<SalesOrderLine> Lines { get; set; } = [];
}

/// <summary>Sales order line.</summary>
public class SalesOrderLine
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public int Sequence { get; set; }
    public string ItemNumber { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal AmountExcludingTax { get; set; }
}

/// <summary>salesQuote.status values.</summary>
public static class QuoteStatus
{
    public const string Draft = "Draft";
    public const string Sent = "Sent";
    public const string Accepted = "Accepted";
    public const string Expired = "Expired";
}
