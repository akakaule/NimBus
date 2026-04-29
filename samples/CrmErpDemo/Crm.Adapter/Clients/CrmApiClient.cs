using System.Net.Http.Json;

namespace Crm.Adapter.Clients;

public sealed class CrmApiClient(HttpClient http) : ICrmApiClient
{
    public async Task LinkErpAsync(Guid accountId, Guid erpCustomerId, string customerNumber, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync(
            $"/api/accounts/{accountId}/link-erp",
            new { ErpCustomerId = erpCustomerId, CustomerNumber = customerNumber },
            ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task UpsertFromErpAsync(Guid erpCustomerId, AccountUpsertPayload payload, CancellationToken ct)
    {
        var response = await http.PutAsJsonAsync(
            $"/api/accounts/external/{erpCustomerId}",
            payload,
            ct);
        response.EnsureSuccessStatusCode();
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
            },
            ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task MarkAccountByErpIdDeletedAsync(Guid erpCustomerId, CancellationToken ct)
    {
        var response = await http.PostAsync($"/api/accounts/external/{erpCustomerId}/deleted", content: null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task MarkContactDeletedAsync(Guid contactId, CancellationToken ct)
    {
        var response = await http.PostAsync($"/api/contacts/{contactId}/deleted", content: null, ct);
        response.EnsureSuccessStatusCode();
    }
}
