namespace BusinessCentral.Adapter.Clients;

/// <summary>
/// The Business Central calls the adapter makes. Shaped like BC online: standard API v2.0 for
/// customers, and the custom CRM API (an AL extension in a real tenant) for CRM's prospects and
/// opportunities. Pointing it at a real BC environment means adding OAuth (client credentials) and the
/// environment's base URL.
/// </summary>
public interface IBusinessCentralClient
{
    /// <summary><c>PUT api/contoso/crm/v1.0/companies({id})/prospects({crmAccountId})</c>: create or update the prospect contact.</summary>
    Task<ProspectUpsertResult> UpsertProspectAsync(Guid crmAccountId, ProspectUpsertBody body, CancellationToken cancellationToken);

    /// <summary><c>PUT api/contoso/crm/v1.0/companies({id})/crmOpportunities({opportunityId})</c>: create or update the CRM opportunity.</summary>
    Task<CrmOpportunityResponse> UpsertCrmOpportunityAsync(Guid opportunityId, CrmOpportunityBody body, CancellationToken cancellationToken);

    /// <summary><c>GET api/v2.0/companies({id})/customers({customerId})</c>; null when BC has no such customer.</summary>
    Task<BcApiCustomer?> GetCustomerAsync(Guid customerId, CancellationToken cancellationToken);

    /// <summary><c>GET api/v2.0/companies({id})/customers({customerId})/customerFinancialDetail</c>.</summary>
    Task<BcApiCustomerFinancialDetail?> GetCustomerFinancialDetailAsync(Guid customerId, CancellationToken cancellationToken);
}

/// <summary>Outcome of a prospect upsert.</summary>
public enum ProspectUpsertResult
{
    /// <summary>BC created the prospect contact.</summary>
    Created,

    /// <summary>BC updated its prospect contact.</summary>
    Updated,

    /// <summary>The prospect has become a BC customer; BC owns its master data and refused the change.</summary>
    OwnedByBusinessCentral,
}

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

public sealed record ProspectUpsertBody(ProspectBody Prospect, ContactPersonBody? ContactPerson);

public sealed record CrmOpportunityBody(
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

public sealed record CrmOpportunityResponse(Guid Id, string Number, string Outcome);

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
