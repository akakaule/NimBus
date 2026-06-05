using System.Net;
using System.Text;
using Newtonsoft.Json;

namespace EnrichmentAgent.Bus;

/// <summary>
/// <see cref="IBusGateway"/> backed by the NimBus agent REST API. Registered as a
/// typed <see cref="HttpClient"/> whose base address resolves the <c>nimbus-ops</c>
/// Aspire resource via service discovery and whose default headers carry the agent
/// identity (<c>X-Agent-Id</c>). All bodies are camelCase JSON (Newtonsoft) to match
/// the API contract.
/// </summary>
public sealed class RestBusGateway : IBusGateway
{
    private readonly HttpClient _http;

    public RestBusGateway(HttpClient http) => _http = http;

    public async Task SubscribeAsync(string eventTypeId, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync(
            "/api/agent/subscribe",
            JsonBody(new { eventTypeId }),
            ct);
        await EnsureSuccessAsync(response, $"Subscribe to '{eventTypeId}'", ct);
    }

    public async Task DefineEventTypeAsync(string eventTypeId, string jsonSchema, string? name, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync(
            "/api/agent/event-types",
            JsonBody(new { eventTypeId, jsonSchema, name }),
            ct);

        // 200 → defined (or idempotently re-confirmed with the same schema).
        // 409 → already defined with a *different* schema. We always pass the same
        // fixed schema, so a 409 can only come from a stale prior run; treat it as
        // "already defined" and move on rather than crash the loop.
        if (response.StatusCode == HttpStatusCode.Conflict)
            return;

        await EnsureSuccessAsync(response, $"Define event type '{eventTypeId}'", ct);
    }

    public async Task<ReceivedMessage?> ReceiveAsync(string? eventTypeId, int waitSeconds, CancellationToken ct = default)
    {
        var query = $"/api/agent/receive?waitSeconds={waitSeconds}";
        if (!string.IsNullOrWhiteSpace(eventTypeId))
            query += $"&eventTypeId={Uri.EscapeDataString(eventTypeId)}";

        using var response = await _http.GetAsync(query, ct);

        // 204 → nothing parked within the long-poll window.
        if (response.StatusCode == HttpStatusCode.NoContent)
            return null;

        await EnsureSuccessAsync(response, "Receive", ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonConvert.DeserializeObject<ReceivedMessage>(json)
            ?? throw new HttpRequestException("Receive returned 200 but an empty/unparseable body.");
    }

    public async Task PublishAsync(string eventTypeId, string payloadJson, string? sessionId, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync(
            "/api/agent/publish",
            JsonBody(new { eventTypeId, payload = payloadJson, sessionId }),
            ct);
        await EnsureSuccessAsync(response, $"Publish '{eventTypeId}'", ct);
    }

    public async Task SettleAsync(HandoffCoordinates coordinates, string outcome, string? result, CancellationToken ct = default)
    {
        var body = new
        {
            coordinates = new
            {
                eventId = coordinates.EventId,
                sessionId = coordinates.SessionId,
                messageId = coordinates.MessageId,
                eventTypeId = coordinates.EventTypeId,
                correlationId = coordinates.CorrelationId,
                originatingMessageId = coordinates.OriginatingMessageId,
            },
            outcome,
            result,
        };

        using var response = await _http.PostAsync(
            "/api/agent/settle",
            JsonBody(body),
            ct);
        await EnsureSuccessAsync(response, $"Settle '{coordinates.EventId}' ({outcome})", ct);
    }

    private static StringContent JsonBody(object body) =>
        new(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var responseBody = string.Empty;
        try { responseBody = await response.Content.ReadAsStringAsync(ct); }
        catch { /* best-effort diagnostics — never crash over a body-read failure */ }

        var detail = string.IsNullOrWhiteSpace(responseBody) ? string.Empty : $" Body: {responseBody}";
        throw new HttpRequestException(
            $"{operation} failed: agent API → {(int)response.StatusCode} {response.ReasonPhrase}.{detail}",
            inner: null,
            statusCode: response.StatusCode);
    }
}
