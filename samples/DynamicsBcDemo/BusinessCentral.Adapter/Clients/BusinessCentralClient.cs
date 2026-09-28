using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicsBcDemo.Contracts.Demo;
using DynamicsBcDemo.Contracts.Http;

namespace BusinessCentral.Adapter.Clients;

/// <summary>
/// Typed HTTP client for the Business Central APIs. It maps every failure to one of three exception
/// types (throttled / unavailable / rejected) so NimBus can treat each the right way. The HTTP
/// client deliberately has no resilience handler (see Program.cs): retries belong to NimBus, where
/// they are visible and audited.
/// </summary>
public sealed class BusinessCentralClient(HttpClient http) : IBusinessCentralClient
{
    private const string ApiName = "Business Central API";
    private static readonly string CrmApi = $"/api/contoso/crm/v1.0/companies({SeedData.CompanyId})";
    private static readonly string StandardApi = $"/api/v2.0/companies({SeedData.CompanyId})";

    public async Task<ProspectUpsertResult> UpsertProspectAsync(Guid crmAccountId, ProspectUpsertBody body, CancellationToken cancellationToken)
    {
        var operation = $"Send the prospect for CRM account {crmAccountId} to Business Central";
        using var response = await SendAsync(
            () => http.PutAsJsonAsync($"{CrmApi}/prospects({crmAccountId})", body, cancellationToken),
            operation);

        if (response.StatusCode == HttpStatusCode.Conflict) return ProspectUpsertResult.OwnedByBusinessCentral;

        await ThrowOnFailureAsync(response, operation, cancellationToken);
        return response.StatusCode == HttpStatusCode.Created ? ProspectUpsertResult.Created : ProspectUpsertResult.Updated;
    }

    public async Task<CrmOpportunityResponse> UpsertCrmOpportunityAsync(Guid opportunityId, CrmOpportunityBody body, CancellationToken cancellationToken)
    {
        var operation = $"Send CRM opportunity {body.Number} to Business Central";
        using var response = await SendAsync(
            () => http.PutAsJsonAsync($"{CrmApi}/crmOpportunities({opportunityId})", body, cancellationToken),
            operation);
        await ThrowOnFailureAsync(response, operation, cancellationToken);
        return await response.Content.ReadFromJsonAsync<CrmOpportunityResponse>(cancellationToken)
            ?? throw new BcUnavailableException($"{operation} failed: {ApiName} returned an empty body.");
    }

    public async Task<BcApiCustomer?> GetCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var operation = $"Read Business Central customer {customerId}";
        using var response = await SendAsync(
            () => http.GetAsync($"{StandardApi}/customers({customerId})", cancellationToken),
            operation);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;

        await ThrowOnFailureAsync(response, operation, cancellationToken);
        return await response.Content.ReadFromJsonAsync<BcApiCustomer>(cancellationToken);
    }

    public async Task<BcApiCustomerFinancialDetail?> GetCustomerFinancialDetailAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var operation = $"Read the financial detail of Business Central customer {customerId}";
        using var response = await SendAsync(
            () => http.GetAsync($"{StandardApi}/customers({customerId})/customerFinancialDetail", cancellationToken),
            operation);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;

        await ThrowOnFailureAsync(response, operation, cancellationToken);
        return await response.Content.ReadFromJsonAsync<BcApiCustomerFinancialDetail>(cancellationToken);
    }

    /// <summary>Sends the request; a connection failure or timeout means BC is unavailable.</summary>
    private static async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send, string operation)
    {
        try
        {
            return await send();
        }
        catch (HttpRequestException ex)
        {
            throw new BcUnavailableException($"{operation} failed: {ApiName} could not be reached ({ex.Message}).", ex);
        }
        catch (TaskCanceledException ex) when (!ex.CancellationToken.IsCancellationRequested)
        {
            throw new BcUnavailableException($"{operation} failed: {ApiName} did not answer in time.", ex);
        }
    }

    /// <summary>Maps a non-success response to the exception type NimBus should see.</summary>
    internal static async Task ThrowOnFailureAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        var message = await response.DescribeFailureAsync(operation, ApiName, cancellationToken);
        var status = (int)response.StatusCode;
        throw status switch
        {
            429 => new BcThrottledException(message, response.Headers.RetryAfter?.Delta),
            408 or 502 or 503 or 504 => new BcUnavailableException(message),
            >= 400 and < 500 => new BcRequestRejectedException(message, status, await ReadErrorCodeAsync(response, cancellationToken)),
            _ => new BcUnavailableException(message),
        };
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var code)
                ? code.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
