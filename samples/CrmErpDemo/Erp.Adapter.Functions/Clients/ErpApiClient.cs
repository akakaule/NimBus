using System.Net.Http.Json;

namespace Erp.Adapter.Functions.Clients;

public sealed class ErpApiClient(HttpClient http) : IErpApiClient
{
    public async Task<ErpCustomerResponse> UpsertCustomerAsync(Guid crmAccountId, CustomerUpsertPayload payload, CancellationToken ct)
    {
        var response = await http.PutAsJsonAsync($"/api/customers/by-crm/{crmAccountId}", payload, ct);
        await EnsureSuccess(
            response,
            $"Upsert ERP customer from CRM account {crmAccountId} (LegalName=\"{payload.LegalName}\")",
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
        await EnsureSuccess(
            response,
            $"Upsert ERP contact {contactId} (CrmAccountId={payload.CrmAccountId})",
            ct);
    }

    public async Task MarkCustomerByCrmIdDeletedAsync(Guid crmAccountId, CancellationToken ct)
    {
        var response = await http.PostAsync($"/api/customers/by-crm/{crmAccountId}/deleted", content: null, ct);
        await EnsureSuccess(
            response,
            $"Mark ERP customer deleted by CRM account {crmAccountId}",
            ct);
    }

    public async Task MarkContactDeletedAsync(Guid contactId, CancellationToken ct)
    {
        var response = await http.PostAsync($"/api/contacts/{contactId}/deleted", content: null, ct);
        await EnsureSuccess(
            response,
            $"Mark ERP contact {contactId} deleted",
            ct);
    }

    // Replaces HttpResponseMessage.EnsureSuccessStatusCode() with an exception
    // that surfaces the operation context, request path, status, and response
    // body — so the WebApp's Failed-message view shows actionable detail rather
    // than just "Response status code does not indicate success: 404 (Not Found)".
    private static async Task EnsureSuccess(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        string body = string.Empty;
        try { body = await response.Content.ReadAsStringAsync(ct); } catch { /* best-effort */ }
        if (body.Length > 500) body = body[..500] + "…";

        var method = response.RequestMessage?.Method.Method ?? "?";
        var path = response.RequestMessage?.RequestUri?.PathAndQuery ?? "?";
        var status = (int)response.StatusCode;
        var reason = response.ReasonPhrase ?? response.StatusCode.ToString();
        var bodyPart = string.IsNullOrWhiteSpace(body) ? "" : $" Body: {body}";

        throw new HttpRequestException(
            $"{operation} failed: ERP API {method} {path} → {status} {reason}.{bodyPart}",
            inner: null,
            statusCode: response.StatusCode);
    }
}
