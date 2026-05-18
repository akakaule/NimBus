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
        await EnsureSuccess(
            response,
            $"Link CRM account {accountId} to ERP customer {erpCustomerId} ({customerNumber})",
            ct);
    }

    public async Task UpsertFromErpAsync(Guid erpCustomerId, AccountUpsertPayload payload, CancellationToken ct)
    {
        var response = await http.PutAsJsonAsync(
            $"/api/accounts/external/{erpCustomerId}",
            payload,
            ct);
        await EnsureSuccess(
            response,
            $"Upsert CRM account from ERP customer {erpCustomerId} (CustomerNumber={payload.CustomerNumber})",
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
            },
            ct);
        await EnsureSuccess(
            response,
            $"Upsert CRM contact {contactId} (ErpCustomerId={payload.ErpCustomerId})",
            ct);
    }

    public async Task MarkAccountByErpIdDeletedAsync(Guid erpCustomerId, CancellationToken ct)
    {
        var response = await http.PostAsync($"/api/accounts/external/{erpCustomerId}/deleted", content: null, ct);
        await EnsureSuccess(
            response,
            $"Mark CRM account deleted by ERP customer {erpCustomerId}",
            ct);
    }

    public async Task MarkContactDeletedAsync(Guid contactId, CancellationToken ct)
    {
        var response = await http.PostAsync($"/api/contacts/{contactId}/deleted", content: null, ct);
        await EnsureSuccess(
            response,
            $"Mark CRM contact {contactId} deleted",
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
            $"{operation} failed: CRM API {method} {path} → {status} {reason}.{bodyPart}",
            inner: null,
            statusCode: response.StatusCode);
    }
}
