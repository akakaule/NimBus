namespace Crm.Adapter.Clients;

public interface ICrmApiClient
{
    Task LinkErpAsync(Guid accountId, Guid erpCustomerId, string customerNumber, CancellationToken ct);
    Task UpsertFromErpAsync(Guid erpCustomerId, AccountUpsertPayload payload, CancellationToken ct);
    Task UpsertContactAsync(Guid contactId, ContactPayload payload, CancellationToken ct);
    Task MarkAccountByErpIdDeletedAsync(Guid erpCustomerId, CancellationToken ct);
    Task MarkContactDeletedAsync(Guid contactId, CancellationToken ct);
}

// ErpCustomerId carries the ERP customer id from the inbound event. The CRM API
// resolves it to a local Account.Id via Accounts.ErpCustomerId before storing
// the contact, so the contact ends up linked to the correct CRM account.
public record ContactPayload(Guid? ErpCustomerId, string FirstName, string LastName, string? Email, string? Phone);
public record AccountUpsertPayload(Guid? CrmAccountId, string LegalName, string? TaxId, string CountryCode, string? CustomerNumber);
