namespace BusinessCentral.Api.Domain;

// Request/response shapes of the custom "CRM integration" API — in a real tenant a small AL
// extension (api/contoso/crm/v1.0) — and of the BC domain operations. JSON is camelCase like the
// standard BC APIs.

/// <summary>Prospect master data, as CRM owns it until the account receives its first quote.</summary>
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

/// <summary>Body of <c>PUT .../prospects({crmAccountId})</c>.</summary>
public sealed record ProspectUpsert(ProspectData Prospect, ContactPersonData? ContactPerson);

/// <summary>What a prospect upsert from CRM did.</summary>
public enum ProspectUpsertOutcome
{
    /// <summary>BC created the prospect contact.</summary>
    Created,

    /// <summary>BC updated its prospect contact with CRM's latest data.</summary>
    Updated,
}

/// <summary>Body of <c>PUT .../crmOpportunities({id})</c>: CRM's opportunity, as BC keeps it for quoting.</summary>
public sealed record CrmOpportunityData(
    string Number,
    string Name,
    Guid CrmAccountId,
    string AccountName,
    Guid? BcCustomerId,
    string SellerEmail,
    decimal? EstimatedValue,
    string CurrencyCode,
    DateTime? EstimatedCloseDate,
    string? ProductGroupCode,
    string Status);

/// <summary>What a CRM opportunity upsert did.</summary>
public enum CrmOpportunityUpsertOutcome
{
    Created,
    Updated,
}

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

/// <summary>What the go-live initial sync sent to CRM.</summary>
public sealed record InitialSyncResult(int ItemCategories, int Customers, int Contacts);
