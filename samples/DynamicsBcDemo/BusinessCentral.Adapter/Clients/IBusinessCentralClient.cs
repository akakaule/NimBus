namespace BusinessCentral.Adapter.Clients;

/// <summary>
/// The Business Central calls the adapter makes. Shaped like BC online: standard API v2.0 for
/// customers, and the custom CRM API (an AL extension in a real tenant) for prospect quotes. Pointing
/// it at a real BC environment means adding OAuth (client credentials) and the environment's base URL.
/// </summary>
public interface IBusinessCentralClient
{
    /// <summary><c>POST api/contoso/crm/v1.0/companies({id})/quoteRequests</c>.</summary>
    Task<QuoteRequestResponse> CreateQuoteRequestAsync(QuoteRequestBody body, CancellationToken cancellationToken);

    /// <summary><c>PATCH api/contoso/crm/v1.0/companies({id})/prospects({crmAccountId})</c>.</summary>
    Task<ProspectUpdateResult> UpdateProspectAsync(Guid crmAccountId, ProspectPatchBody body, CancellationToken cancellationToken);

    /// <summary><c>GET api/v2.0/companies({id})/customers({customerId})</c>; null when BC has no such customer.</summary>
    Task<BcApiCustomer?> GetCustomerAsync(Guid customerId, CancellationToken cancellationToken);

    /// <summary><c>GET api/v2.0/companies({id})/customers({customerId})/customerFinancialDetail</c>.</summary>
    Task<BcApiCustomerFinancialDetail?> GetCustomerFinancialDetailAsync(Guid customerId, CancellationToken cancellationToken);
}

/// <summary>Outcome of a prospect update.</summary>
public enum ProspectUpdateResult
{
    Updated,

    /// <summary>BC doesn't know the prospect (it never asked for a quote): nothing to update.</summary>
    NotInBusinessCentral,

    /// <summary>The prospect has become a BC customer; BC owns its master data and refused the update.</summary>
    OwnedByBusinessCentral,
}

public sealed record QuoteRequestBody(
    Guid CrmOpportunityId,
    Guid CrmAccountId,
    string OpportunityNumber,
    string OpportunityName,
    int RequestRevision,
    string? CustomerNumber,
    ProspectBody Prospect,
    ContactPersonBody? ContactPerson,
    string SellerEmail,
    string CurrencyCode,
    IReadOnlyList<QuoteRequestLineBody> Lines);

public sealed record ProspectBody(
    string Name,
    string? VatRegistrationNumber,
    string? AddressLine1,
    string? City,
    string? PostalCode,
    string CountryCode,
    string? PhoneNumber,
    string? Website);

public sealed record ContactPersonBody(string? Name, string? Email, string? Phone);

public sealed record QuoteRequestLineBody(string ItemNumber, decimal Quantity, string? Description);

public sealed record ProspectPatchBody(ProspectBody Prospect, ContactPersonBody? ContactPerson);

public sealed record QuoteRequestResponse(
    Guid Id,
    string Number,
    string Status,
    string? SellToContactNumber,
    string? CustomerNumber,
    decimal TotalAmountExcludingTax,
    string Outcome);

/// <summary>The API v2.0 customer fields the adapter reads.</summary>
public sealed record BcApiCustomer(
    Guid Id,
    string Number,
    string DisplayName,
    decimal BalanceDue,
    decimal CreditLimit,
    string? Blocked,
    string? CurrencyCode);

/// <summary>The customerFinancialDetail fields the adapter reads.</summary>
public sealed record BcApiCustomerFinancialDetail(Guid Id, string Number, decimal Balance, decimal OverdueAmount);
