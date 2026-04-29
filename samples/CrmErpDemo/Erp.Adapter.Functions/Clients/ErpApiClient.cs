using System.Net.Http.Json;

namespace Erp.Adapter.Functions.Clients;

public sealed class ErpApiClient(HttpClient http) : IErpApiClient
{
    public async Task<ErpCustomerResponse> UpsertCustomerAsync(Guid crmAccountId, CustomerUpsertPayload payload, CancellationToken ct)
    {
        var response = await http.PutAsJsonAsync($"/api/customers/by-crm/{crmAccountId}", payload, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ErpCustomerResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("ERP API returned empty customer payload.");
    }

    public async Task UpsertContactAsync(Guid contactId, ContactUpsertPayload payload, CancellationToken ct)
    {
        var response = await http.PutAsJsonAsync(
            $"/api/contacts/upsert/{contactId}",
            new
            {
                Id = contactId,
                payload.CrmAccountId,
                payload.FirstName,
                payload.LastName,
                payload.Email,
                payload.Phone,
            },
            ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task MarkCustomerByCrmIdDeletedAsync(Guid crmAccountId, CancellationToken ct)
    {
        var response = await http.PostAsync($"/api/customers/by-crm/{crmAccountId}/deleted", content: null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task MarkContactDeletedAsync(Guid contactId, CancellationToken ct)
    {
        var response = await http.PostAsync($"/api/contacts/{contactId}/deleted", content: null, ct);
        response.EnsureSuccessStatusCode();
    }
}
