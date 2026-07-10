using System.Net.Http.Json;
using CrmErpDemo.Contracts.Http;

namespace Crm.Adapter.Clients;

public sealed class CrmApiClient(HttpClient http) : ICrmApiClient
{
    private const string ApiName = "CRM API";

    public async Task LinkErpAsync(Guid accountId, Guid erpCustomerId, string customerNumber, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync(
            $"/api/accounts/{accountId}/link-erp",
            new { ErpCustomerId = erpCustomerId, CustomerNumber = customerNumber },
            ct);
        await response.EnsureSuccessOrThrowAsync(
            $"Link CRM account {accountId} to ERP customer {erpCustomerId} ({customerNumber})",
            ApiName,
            ct);
    }

    public async Task UpsertFromErpAsync(Guid erpCustomerId, AccountUpsertPayload payload, CancellationToken ct)
    {
        var response = await http.PutAsJsonAsync(
            $"/api/accounts/external/{erpCustomerId}",
            payload,
            ct);
        await response.EnsureSuccessOrThrowAsync(
            $"Upsert CRM account from ERP customer {erpCustomerId} (CustomerNumber={payload.CustomerNumber})",
            ApiName,
            ct);
    }

    public async Task UpsertContactAsync(Guid contactId, ContactPayload payload, CancellationToken ct)
    {
        var response = await http.PutAsJsonAsync(
            $"/api/contacts/upsert/{contactId}",
            new
            {
                Id = contactId,
                payload.ErpCustomerId,
                payload.FirstName,
                payload.LastName,
                payload.Email,
                payload.Phone,
                payload.Origin,
            },
            ct);
        await response.EnsureSuccessOrThrowAsync(
            $"Upsert CRM contact {contactId} (ErpCustomerId={payload.ErpCustomerId})",
            ApiName,
            ct);
    }

    public async Task MarkAccountByErpIdDeletedAsync(Guid erpCustomerId, CancellationToken ct)
    {
        var response = await http.PostAsync($"/api/accounts/external/{erpCustomerId}/deleted", content: null, ct);
        await response.EnsureSuccessOrThrowAsync(
            $"Mark CRM account deleted by ERP customer {erpCustomerId}",
            ApiName,
            ct);
    }

    public async Task MarkContactDeletedAsync(Guid contactId, CancellationToken ct)
    {
        var response = await http.PostAsync($"/api/contacts/{contactId}/deleted", content: null, ct);
        await response.EnsureSuccessOrThrowAsync(
            $"Mark CRM contact {contactId} deleted",
            ApiName,
            ct);
    }
}
