using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NimBus.Mcp.Http;

/// <summary>
/// Typed <see cref="HttpClient"/> implementation of <see cref="INimBusAgentApi"/>.
/// The underlying <see cref="HttpClient"/> must be configured with:
/// <list type="bullet">
///   <item><description><see cref="HttpClient.BaseAddress"/> set to the NimBus WebApp base URL.</description></item>
///   <item><description>Default request header <c>X-Agent-Id</c> set to the agent identifier.</description></item>
/// </list>
/// </summary>
public sealed class NimBusAgentApiClient : INimBusAgentApi
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    /// <summary>Initialises a new instance backed by the provided <paramref name="httpClient"/>.</summary>
    public NimBusAgentApiClient(HttpClient httpClient)
    {
        _http = httpClient;
    }

    /// <inheritdoc/>
    public async Task<AgentCatalog?> GetCatalogAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("api/agent/catalog", ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<AgentCatalog>(s_jsonOptions, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<EventTypeInfo?> DefineEventTypeAsync(DefineEventTypeRequest req, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("api/agent/event-types", req, s_jsonOptions, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<EventTypeInfo>(s_jsonOptions, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SubscribeAsync(AgentSubscribeRequest req, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("api/agent/subscribe", req, s_jsonOptions, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<AgentReceivedMessage?> ReceiveAsync(string? eventTypeId, int? waitSeconds, CancellationToken ct = default)
    {
        var url = BuildReceiveUrl(eventTypeId, waitSeconds);
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<AgentReceivedMessage>(s_jsonOptions, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task PublishAsync(AgentPublishRequest req, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("api/agent/publish", req, s_jsonOptions, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SettleAsync(AgentSettleRequest req, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("api/agent/settle", req, s_jsonOptions, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<string> SearchFailuresAsync(string query, CancellationToken ct = default)
    {
        // POST /api/messages/search — send eventTypeId hint as array element
        var msgBody = new MessageSearchBody(new MessageSearchFilterBody(new[] { query }));
        string msgJson;
        using (var msgResponse = await _http.PostAsJsonAsync("api/messages/search", msgBody, s_jsonOptions, ct).ConfigureAwait(false))
        {
            await EnsureSuccessAsync(msgResponse, ct).ConfigureAwait(false);
            msgJson = await msgResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        // POST /api/audits/search — send eventTypeId hint as string
        var auditBody = new AuditSearchBody(new AuditSearchFilterBody(query));
        string auditJson;
        using (var auditResponse = await _http.PostAsJsonAsync("api/audits/search", auditBody, s_jsonOptions, ct).ConfigureAwait(false))
        {
            await EnsureSuccessAsync(auditResponse, ct).ConfigureAwait(false);
            auditJson = await auditResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        return $"{{\"messages\":{msgJson},\"audits\":{auditJson}}}";
    }

    /// <inheritdoc/>
    public async Task<MappingInfo?> ProposeMappingAsync(ProposeMappingRequest req, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("api/agent/mappings", req, s_jsonOptions, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<MappingInfo>(s_jsonOptions, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MappingInfo>> ListMappingsAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("api/agent/mappings", ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<MappingInfo[]>(s_jsonOptions, ct).ConfigureAwait(false) ?? [];
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string BuildReceiveUrl(string? eventTypeId, int? waitSeconds)
    {
        var hasEventType = !string.IsNullOrEmpty(eventTypeId);
        var hasWait = waitSeconds.HasValue;

        if (!hasEventType && !hasWait)
        {
            return "api/agent/receive";
        }

        var qs = new System.Text.StringBuilder("api/agent/receive?");
        if (hasEventType)
        {
            qs.Append("eventTypeId=").Append(Uri.EscapeDataString(eventTypeId!));
            if (hasWait) qs.Append('&');
        }

        if (hasWait)
        {
            qs.Append("waitSeconds=").Append(waitSeconds!.Value);
        }

        return qs.ToString();
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct = default)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new NimBusApiException((int)response.StatusCode, body);
    }
}
