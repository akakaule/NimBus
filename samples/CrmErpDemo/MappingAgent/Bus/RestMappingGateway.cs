using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MappingAgent.Bus;

/// <summary>
/// <see cref="IMappingBusGateway"/> backed by the NimBus agent REST API. Registered as a
/// typed <see cref="HttpClient"/> whose base address resolves the <c>nimbus-ops</c> Aspire
/// resource via service discovery. Mirrors <c>RestBusGateway</c> from the EnrichmentAgent.
/// </summary>
public sealed class RestMappingGateway : IMappingBusGateway
{
    private readonly HttpClient _http;

    public RestMappingGateway(HttpClient http) => _http = http;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CatalogEntry>> GetCatalogAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("/api/agent/catalog", ct);
        await EnsureSuccessAsync(response, "GetCatalog", ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        var entries = JsonConvert.DeserializeObject<List<CatalogEntry>>(json);
        return entries ?? new List<CatalogEntry>();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> GetSamplePayloadsAsync(
        string eventTypeId, int maxCount, CancellationToken ct = default)
    {
        var url = $"/api/messages/search?eventTypeId={Uri.EscapeDataString(eventTypeId)}&maxCount={maxCount}";
        using var response = await _http.GetAsync(url, ct);

        // 404 or 204 → no messages found — return empty rather than throw.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
            return Array.Empty<string>();
        if (!response.IsSuccessStatusCode)
            return Array.Empty<string>();

        var json = await response.Content.ReadAsStringAsync(ct);

        // The search response contains a "messages" array; extract EventJson from each entry.
        var payloads = new List<string>();
        try
        {
            var root = JObject.Parse(json);
            var messages = root["messages"] as JArray ?? root["Messages"] as JArray;
            if (messages != null)
            {
                foreach (var msg in messages.Take(maxCount))
                {
                    var eventJson = msg["messageContent"]?["eventContent"]?["eventJson"]?.ToString()
                        ?? msg["MessageContent"]?["EventContent"]?["EventJson"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(eventJson))
                        payloads.Add(eventJson);
                }
            }
        }
        catch
        {
            // Best-effort: if parsing fails, return empty so the agent synthesizes from schema.
        }

        return payloads;
    }

    /// <inheritdoc/>
    public async Task<string> ProposeMappingAsync(
        string sourceEventTypeId,
        string targetEventTypeId,
        string transform,
        string rationale,
        string sourceSchemaHash,
        string? workedExamplesJson,
        CancellationToken ct = default)
    {
        var body = new
        {
            sourceEventTypeId,
            targetEventTypeId,
            transform,
            rationale,
            sourceSchemaHash,
            workedExamplesJson,
            createdBy = "mapping-agent",
        };

        using var response = await _http.PostAsync(
            "/api/agent/mappings",
            JsonBody(body),
            ct);

        await EnsureSuccessAsync(response, $"ProposeMapping '{sourceEventTypeId}' → '{targetEventTypeId}'", ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        // The response body is the created EventMapping JSON; extract the id.
        try
        {
            var obj = JObject.Parse(json);
            var id = obj["id"]?.ToString() ?? obj["Id"]?.ToString();
            if (!string.IsNullOrWhiteSpace(id))
                return id;
        }
        catch { /* fall through */ }

        return $"{sourceEventTypeId}->{targetEventTypeId}";
    }

    private static StringContent JsonBody(object body) =>
        new(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var responseBody = string.Empty;
        try { responseBody = await response.Content.ReadAsStringAsync(ct); }
        catch { /* best-effort */ }

        var detail = string.IsNullOrWhiteSpace(responseBody) ? string.Empty : $" Body: {responseBody}";
        throw new HttpRequestException(
            $"{operation} failed: agent API → {(int)response.StatusCode} {response.ReasonPhrase}.{detail}",
            inner: null,
            statusCode: response.StatusCode);
    }
}
