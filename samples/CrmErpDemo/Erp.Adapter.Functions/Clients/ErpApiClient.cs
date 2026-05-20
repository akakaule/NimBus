using System.Net.Http.Json;
using CrmErpDemo.Contracts.Http;

namespace Erp.Adapter.Functions.Clients;

public sealed class ErpApiClient(HttpClient http) : IErpApiClient
{
    private const string ApiName = "ERP API";

    public async Task<ErpCustomerResponse> UpsertCustomerAsync(Guid crmAccountId, CustomerUpsertPayload payload, CancellationToken ct)
    {
        var response = await http.PutAsJsonAsync($"/api/customers/by-crm/{crmAccountId}", payload, ct);
        await response.EnsureSuccessOrThrowAsync(
            $"Upsert ERP customer from CRM account {crmAccountId} (LegalName=\"{payload.LegalName}\")",
            ApiName,
            ct);
        return await response.Content.ReadFromJsonAsync<ErpCustomerResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException(
                $"ERP API returned an empty body for upsert of CRM account {crmAccountId}. Expected ErpCustomerResponse.");
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
        await response.EnsureSuccessOrThrowAsync(
            $"Upsert ERP contact {contactId} (CrmAccountId={payload.CrmAccountId})",
            ApiName,
            ct);
    }

    public async Task MarkCustomerByCrmIdDeletedAsync(Guid crmAccountId, CancellationToken ct)
    {
        var response = await http.PostAsync($"/api/customers/by-crm/{crmAccountId}/deleted", content: null, ct);
        await response.EnsureSuccessOrThrowAsync(
            $"Mark ERP customer deleted by CRM account {crmAccountId}",
            ApiName,
            ct);
    }

    public async Task MarkContactDeletedAsync(Guid contactId, CancellationToken ct)
    {
        var response = await http.PostAsync($"/api/contacts/{contactId}/deleted", content: null, ct);
        await response.EnsureSuccessOrThrowAsync(
            $"Mark ERP contact {contactId} deleted",
            ApiName,
            ct);
    }
}
