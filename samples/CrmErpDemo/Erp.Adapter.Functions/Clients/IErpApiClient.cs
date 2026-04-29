namespace Erp.Adapter.Functions.Clients;

public interface IErpApiClient
{
    Task<ErpCustomerResponse> UpsertCustomerAsync(Guid crmAccountId, CustomerUpsertPayload payload, CancellationToken ct);
    Task UpsertContactAsync(Guid contactId, ContactUpsertPayload payload, CancellationToken ct);
    Task MarkCustomerByCrmIdDeletedAsync(Guid crmAccountId, CancellationToken ct);
    Task MarkContactDeletedAsync(Guid contactId, CancellationToken ct);
}

public record CustomerUpsertPayload(string LegalName, string? TaxId, string CountryCode);
// CrmAccountId carries the CRM account id from the inbound event. The ERP API
// resolves it to a local Customer.Id via Customers.CrmAccountId before storing
// the contact, so the contact ends up linked to the correct ERP customer.
public record ContactUpsertPayload(Guid? CrmAccountId, string FirstName, string LastName, string? Email, string? Phone);
public record ErpCustomerResponse(Guid Id, string CustomerNumber, string LegalName);
