namespace BusinessCentral.Api.Domain;

// Request/response shapes of the custom "CRM integration" API — in a real tenant a small AL
// extension (api/contoso/crm/v1.0). JSON is camelCase like the standard BC APIs.

/// <summary>Body of <c>POST .../quoteRequests</c>: create (or re-apply) the quote for a CRM opportunity.</summary>
public sealed record QuoteRequest(
    Guid CrmOpportunityId,
    Guid CrmAccountId,
    string OpportunityNumber,
    string OpportunityName,
    int RequestRevision,
    string? CustomerNumber,
    ProspectData Prospect,
    ContactPersonData? ContactPerson,
    string SellerEmail,
    string CurrencyCode,
    IReadOnlyList<QuoteRequestLine> Lines);

/// <summary>Prospect master data, as CRM owns it until the prospect buys.</summary>
public sealed record ProspectData(
    string Name,
    string? VatRegistrationNumber,
    string? AddressLine1,
    string? City,
    string? PostalCode,
    string CountryCode,
    string? PhoneNumber,
    string? Website);

/// <summary>The contact person at the prospect.</summary>
public sealed record ContactPersonData(string? Name, string? Email, string? Phone);

/// <summary>One line of a quote request.</summary>
public sealed record QuoteRequestLine(string ItemNumber, decimal Quantity, string? Description);

/// <summary>What a quote request did.</summary>
public enum QuoteRequestOutcome
{
    /// <summary>A new draft quote was created.</summary>
    Created,

    /// <summary>The opportunity's open draft quote was re-quoted from a newer request revision.</summary>
    Updated,

    /// <summary>The opportunity already has an open quote for this revision; nothing changed.</summary>
    AlreadyExists,
}

/// <summary>Result of a quote request.</summary>
public sealed record QuoteRequestResult(Data.SalesQuote Quote, QuoteRequestOutcome Outcome);

/// <summary>A quote line as edited on the BC quote card.</summary>
public sealed record QuoteLineEdit(string ItemNumber, decimal Quantity, decimal UnitPrice, decimal DiscountPercent);

/// <summary>Customer card fields BC users can edit.</summary>
public sealed record CustomerEdit(
    string DisplayName,
    string? AddressLine1,
    string? City,
    string? PostalCode,
    string CountryCode,
    string? PhoneNumber,
    string? Email,
    string? Website,
    string? SalespersonCode,
    decimal CreditLimit,
    string? Blocked,
    string? PaymentTermsCode);

/// <summary>Result of Make Order.</summary>
public sealed record MakeOrderResult(Data.SalesOrder Order, Data.Customer Customer, bool CustomerCreated);

/// <summary>Result of a prospect update from CRM.</summary>
public enum ProspectUpdateOutcome
{
    Updated,

    /// <summary>BC has no prospect contact for the CRM account (CRM-only prospect).</summary>
    NotFound,
}
