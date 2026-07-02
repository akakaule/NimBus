#pragma warning disable CA1707, CA2007

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Mcp.Http;

namespace NimBus.Mcp.Tests;

/// <summary>
/// Verifies the HTTP shape (verb, path, headers, query string, body) produced by
/// <see cref="NimBusAgentApiClient"/> without making real network calls.
/// </summary>
[TestClass]
public class NimBusAgentApiClientTests
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    // ── Factory ───────────────────────────────────────────────────────────────

    private static (NimBusAgentApiClient client, FakeHandler handler) CreateClient(
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string? responseBody = null)
    {
        var handler = new FakeHandler(statusCode, responseBody ?? "{}");
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };
        http.DefaultRequestHeaders.Add("X-Agent-Id", "test-agent");
        var client = new NimBusAgentApiClient(http);
        return (client, handler);
    }

    // ── GetCatalogAsync ───────────────────────────────────────────────────────

    [TestMethod]
    public async Task GetCatalogAsync_UsesGetVerb_AndCorrectPath()
    {
        var catalogJson = JsonSerializer.Serialize(
            new AgentCatalog(["ep1"], [new("order.created", "Order Created", null, null)]),
            s_json);
        var (client, handler) = CreateClient(responseBody: catalogJson);

        var result = await client.GetCatalogAsync();

        Assert.AreEqual(HttpMethod.Get, handler.CapturedRequest!.Method);
        Assert.AreEqual("api/agent/catalog", handler.CapturedRequest.RequestUri!.PathAndQuery.TrimStart('/'));
    }

    [TestMethod]
    public async Task GetCatalogAsync_SetsAgentIdHeader()
    {
        var catalogJson = JsonSerializer.Serialize(
            new AgentCatalog([], []),
            s_json);
        var (client, handler) = CreateClient(responseBody: catalogJson);

        await client.GetCatalogAsync();

        Assert.IsTrue(handler.CapturedRequest!.Headers.Contains("X-Agent-Id"),
            "X-Agent-Id header should be present");
        Assert.AreEqual("test-agent",
            handler.CapturedRequest.Headers.GetValues("X-Agent-Id").Single());
    }

    [TestMethod]
    public async Task GetCatalogAsync_DeserializesAgentCatalog()
    {
        var catalog = new AgentCatalog(["ep1", "ep2"],
            [new("order.created", "Order Created", "{}", "An order event")]);
        var json = JsonSerializer.Serialize(catalog, s_json);
        var (client, _) = CreateClient(responseBody: json);

        var result = await client.GetCatalogAsync();

        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.Endpoints.Length);
        Assert.AreEqual("order.created", result.EventTypes[0].EventTypeId);
    }

    // ── DefineEventTypeAsync ──────────────────────────────────────────────────

    [TestMethod]
    public async Task DefineEventTypeAsync_UsesPostVerb_AndCorrectPath()
    {
        var (client, handler) = CreateClient();

        await client.DefineEventTypeAsync(
            new DefineEventTypeRequest("order.created", "{}", "Order", null, null));

        Assert.AreEqual(HttpMethod.Post, handler.CapturedRequest!.Method);
        Assert.AreEqual("api/agent/event-types",
            handler.CapturedRequest.RequestUri!.PathAndQuery.TrimStart('/'));
    }

    [TestMethod]
    public async Task DefineEventTypeAsync_SendsCorrectJsonBody()
    {
        var (client, handler) = CreateClient();
        var req = new DefineEventTypeRequest("order.created", "{\"type\":\"object\"}", "Order", "desc", "$.id");

        await client.DefineEventTypeAsync(req);

        var body = await ReadBodyAsync(handler);
        using var doc = JsonDocument.Parse(body);
        Assert.AreEqual("order.created", doc.RootElement.GetProperty("eventTypeId").GetString());
        Assert.AreEqual("{\"type\":\"object\"}", doc.RootElement.GetProperty("jsonSchema").GetString());
        Assert.AreEqual("$.id", doc.RootElement.GetProperty("sessionKeyPath").GetString());
    }

    [TestMethod]
    public async Task DefineEventTypeAsync_409_ThrowsNimBusApiException()
    {
        var (client, _) = CreateClient(HttpStatusCode.Conflict, "Schema conflict");

        var ex = await Assert.ThrowsExceptionAsync<NimBusApiException>(
            () => client.DefineEventTypeAsync(
                new DefineEventTypeRequest("order.created", "{}", null, null, null)));

        Assert.AreEqual(409, ex.StatusCode);
        Assert.AreEqual("Schema conflict", ex.Body);
    }

    // ── SubscribeAsync ────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SubscribeAsync_UsesPostVerb_AndCorrectPath()
    {
        var (client, handler) = CreateClient();

        await client.SubscribeAsync(new AgentSubscribeRequest("order.created"));

        Assert.AreEqual(HttpMethod.Post, handler.CapturedRequest!.Method);
        Assert.AreEqual("api/agent/subscribe",
            handler.CapturedRequest.RequestUri!.PathAndQuery.TrimStart('/'));
    }

    [TestMethod]
    public async Task SubscribeAsync_SendsEventTypeIdInBody()
    {
        var (client, handler) = CreateClient();

        await client.SubscribeAsync(new AgentSubscribeRequest("order.created"));

        var body = await ReadBodyAsync(handler);
        using var doc = JsonDocument.Parse(body);
        Assert.AreEqual("order.created", doc.RootElement.GetProperty("eventTypeId").GetString());
    }

    // ── ReceiveAsync ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ReceiveAsync_204_ReturnsNull()
    {
        var (client, handler) = CreateClient(HttpStatusCode.NoContent, string.Empty);

        var result = await client.ReceiveAsync(null, null);

        Assert.IsNull(result);
        Assert.AreEqual(HttpMethod.Get, handler.CapturedRequest!.Method);
    }

    [TestMethod]
    public async Task ReceiveAsync_200_DeserializesAgentReceivedMessage()
    {
        var msg = new AgentReceivedMessage(
            "order.created",
            "{\"orderId\":42}",
            new HandoffCoordinates("e1", "s1", "m1", "order.created", "c1", "o1"));
        var json = JsonSerializer.Serialize(msg, s_json);
        var (client, _) = CreateClient(responseBody: json);

        var result = await client.ReceiveAsync("order.created", 10);

        Assert.IsNotNull(result);
        Assert.AreEqual("order.created", result.EventTypeId);
        Assert.AreEqual("e1", result.Coordinates.EventId);
    }

    [TestMethod]
    public async Task ReceiveAsync_WithEventTypeIdAndWaitSeconds_AppendsQueryString()
    {
        var (client, handler) = CreateClient(HttpStatusCode.NoContent, string.Empty);

        await client.ReceiveAsync("order.created", 30);

        var uriStr = handler.CapturedRequest!.RequestUri!.ToString();
        StringAssert.Contains(uriStr, "eventTypeId=order.created");
        StringAssert.Contains(uriStr, "waitSeconds=30");
    }

    [TestMethod]
    public async Task ReceiveAsync_WithNoParams_UsesPathOnly()
    {
        var (client, handler) = CreateClient(HttpStatusCode.NoContent, string.Empty);

        await client.ReceiveAsync(null, null);

        Assert.AreEqual("api/agent/receive",
            handler.CapturedRequest!.RequestUri!.PathAndQuery.TrimStart('/'));
    }

    [TestMethod]
    public async Task ReceiveAsync_WithOnlyEventTypeId_AppendsEventTypeIdOnly()
    {
        var (client, handler) = CreateClient(HttpStatusCode.NoContent, string.Empty);

        await client.ReceiveAsync("order.created", null);

        var pq = handler.CapturedRequest!.RequestUri!.PathAndQuery.TrimStart('/');
        StringAssert.Contains(pq, "eventTypeId=order.created");
        Assert.IsFalse(pq.Contains("waitSeconds", StringComparison.Ordinal), "waitSeconds should not be in query when null");
    }

    // ── PublishAsync ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task PublishAsync_UsesPostVerb_AndCorrectPath()
    {
        var (client, handler) = CreateClient();

        await client.PublishAsync(new AgentPublishRequest("order.created", "{}", null));

        Assert.AreEqual(HttpMethod.Post, handler.CapturedRequest!.Method);
        Assert.AreEqual("api/agent/publish",
            handler.CapturedRequest.RequestUri!.PathAndQuery.TrimStart('/'));
    }

    [TestMethod]
    public async Task PublishAsync_400_ThrowsNimBusApiException()
    {
        var (client, _) = CreateClient(HttpStatusCode.BadRequest, "Schema validation failed");

        var ex = await Assert.ThrowsExceptionAsync<NimBusApiException>(
            () => client.PublishAsync(new AgentPublishRequest("order.created", "{}", null)));

        Assert.AreEqual(400, ex.StatusCode);
        Assert.AreEqual("Schema validation failed", ex.Body);
    }

    [TestMethod]
    public async Task PublishAsync_404_ThrowsNimBusApiException()
    {
        var (client, _) = CreateClient(HttpStatusCode.NotFound, "Event type not found");

        var ex = await Assert.ThrowsExceptionAsync<NimBusApiException>(
            () => client.PublishAsync(new AgentPublishRequest("unknown.type", "{}", null)));

        Assert.AreEqual(404, ex.StatusCode);
        Assert.AreEqual("Event type not found", ex.Body);
    }

    [TestMethod]
    public async Task PublishAsync_SendsCorrectBody()
    {
        var (client, handler) = CreateClient();

        await client.PublishAsync(new AgentPublishRequest("order.created", "{\"id\":1}", "session-42"));

        var body = await ReadBodyAsync(handler);
        using var doc = JsonDocument.Parse(body);
        Assert.AreEqual("order.created", doc.RootElement.GetProperty("eventTypeId").GetString());
        Assert.AreEqual("{\"id\":1}", doc.RootElement.GetProperty("payload").GetString());
        Assert.AreEqual("session-42", doc.RootElement.GetProperty("sessionId").GetString());
    }

    // ── SettleAsync ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SettleAsync_UsesPostVerb_AndCorrectPath()
    {
        var (client, handler) = CreateClient();

        await client.SettleAsync(new AgentSettleRequest(
            new HandoffCoordinates("e1", "s1", "m1", "order.created", "c1", "o1"),
            "complete", null, null, null));

        Assert.AreEqual(HttpMethod.Post, handler.CapturedRequest!.Method);
        Assert.AreEqual("api/agent/settle",
            handler.CapturedRequest.RequestUri!.PathAndQuery.TrimStart('/'));
    }

    [TestMethod]
    public async Task SettleAsync_SendsOutcomeInBody()
    {
        var (client, handler) = CreateClient();

        await client.SettleAsync(new AgentSettleRequest(
            new HandoffCoordinates("e1", "s1", "m1", "order.created", "c1", "o1"),
            "fail", null, "Something went wrong", "ProcessingError"));

        var body = await ReadBodyAsync(handler);
        using var doc = JsonDocument.Parse(body);
        Assert.AreEqual("fail", doc.RootElement.GetProperty("outcome").GetString());
        Assert.AreEqual("Something went wrong", doc.RootElement.GetProperty("errorText").GetString());
    }

    // ── SearchFailuresAsync ───────────────────────────────────────────────────

    [TestMethod]
    public async Task SearchFailuresAsync_PostsToMessagesSearch_AndAuditsSearch()
    {
        // Two requests go out — the fake handler records only the last; we use a multi-handler.
        var multiHandler = new MultiRequestFakeHandler([
            (HttpStatusCode.OK, "{\"items\":[]}"),
            (HttpStatusCode.OK, "{\"items\":[]}")
        ]);
        var http = new HttpClient(multiHandler) { BaseAddress = new Uri("http://localhost:5000/") };
        http.DefaultRequestHeaders.Add("X-Agent-Id", "test-agent");
        var client = new NimBusAgentApiClient(http);

        var result = await client.SearchFailuresAsync("order.created");

        Assert.AreEqual(2, multiHandler.Requests.Count);
        Assert.AreEqual(HttpMethod.Post, multiHandler.Requests[0].Method);
        Assert.AreEqual(HttpMethod.Post, multiHandler.Requests[1].Method);
        Assert.IsTrue(multiHandler.Requests[0].RequestUri!.ToString().Contains("messages/search", StringComparison.Ordinal),
            "First request should go to /api/messages/search");
        Assert.IsTrue(multiHandler.Requests[1].RequestUri!.ToString().Contains("audits/search", StringComparison.Ordinal),
            "Second request should go to /api/audits/search");
    }

    [TestMethod]
    public async Task SearchFailuresAsync_ReturnsCombinedJson()
    {
        var multiHandler = new MultiRequestFakeHandler([
            (HttpStatusCode.OK, "{\"messages_data\":true}"),
            (HttpStatusCode.OK, "{\"audits_data\":true}")
        ]);
        var http = new HttpClient(multiHandler) { BaseAddress = new Uri("http://localhost:5000/") };
        http.DefaultRequestHeaders.Add("X-Agent-Id", "test-agent");
        var client = new NimBusAgentApiClient(http);

        var result = await client.SearchFailuresAsync("order.created");

        StringAssert.Contains(result, "\"messages\"");
        StringAssert.Contains(result, "\"audits\"");
    }

    // ── ProposeMappingAsync ───────────────────────────────────────────────────

    [TestMethod]
    public async Task ProposeMappingAsync_UsesPostVerb_AndCorrectPath()
    {
        var (client, handler) = CreateClient();

        await client.ProposeMappingAsync(
            new ProposeMappingRequest("src.event.v1", "tgt.event.v1", "$$", "hash123"));

        Assert.AreEqual(HttpMethod.Post, handler.CapturedRequest!.Method);
        Assert.AreEqual("api/agent/mappings",
            handler.CapturedRequest.RequestUri!.PathAndQuery.TrimStart('/'));
    }

    [TestMethod]
    public async Task ProposeMappingAsync_SendsCorrectJsonBody()
    {
        var (client, handler) = CreateClient();
        var req = new ProposeMappingRequest(
            "marketing.lead.created.v1",
            "erp.customer.upsert.v1",
            "$$.name",
            "abc123",
            "Initial mapping");

        await client.ProposeMappingAsync(req);

        var body = await ReadBodyAsync(handler);
        using var doc = JsonDocument.Parse(body);
        Assert.AreEqual("marketing.lead.created.v1", doc.RootElement.GetProperty("sourceEventTypeId").GetString());
        Assert.AreEqual("erp.customer.upsert.v1", doc.RootElement.GetProperty("targetEventTypeId").GetString());
        Assert.AreEqual("$$.name", doc.RootElement.GetProperty("transform").GetString());
        Assert.AreEqual("abc123", doc.RootElement.GetProperty("sourceSchemaHash").GetString());
        Assert.AreEqual("Initial mapping", doc.RootElement.GetProperty("rationale").GetString());
    }

    [TestMethod]
    public async Task ProposeMappingAsync_DeserializesMappingInfo()
    {
        var mapping = new MappingInfo("map-1", "src.v1", "tgt.v1", "$$", null, "Draft", 1);
        var json = JsonSerializer.Serialize(mapping, s_json);
        var (client, _) = CreateClient(responseBody: json);

        var result = await client.ProposeMappingAsync(
            new ProposeMappingRequest("src.v1", "tgt.v1", "$$", "hash"));

        Assert.IsNotNull(result);
        Assert.AreEqual("map-1", result!.Id);
        Assert.AreEqual("Draft", result.State);
    }

    [TestMethod]
    public async Task ProposeMappingAsync_404_ThrowsNimBusApiException()
    {
        var (client, _) = CreateClient(HttpStatusCode.NotFound, "Source event type not found");

        var ex = await Assert.ThrowsExceptionAsync<NimBusApiException>(
            () => client.ProposeMappingAsync(
                new ProposeMappingRequest("unknown.type", "tgt.v1", "$$", "hash")));

        Assert.AreEqual(404, ex.StatusCode);
        Assert.AreEqual("Source event type not found", ex.Body);
    }

    // ── ListMappingsAsync ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task ListMappingsAsync_UsesGetVerb_AndCorrectPath()
    {
        var (client, handler) = CreateClient(responseBody: "[]");

        await client.ListMappingsAsync();

        Assert.AreEqual(HttpMethod.Get, handler.CapturedRequest!.Method);
        Assert.AreEqual("api/agent/mappings",
            handler.CapturedRequest.RequestUri!.PathAndQuery.TrimStart('/'));
    }

    [TestMethod]
    public async Task ListMappingsAsync_DeserializesMappingList()
    {
        var mappings = new[]
        {
            new MappingInfo("map-1", "src.v1", "tgt.v1", "$$", null, "Draft", 1),
            new MappingInfo("map-2", "a.v1", "b.v1", "$$.id", "reason", "Active", 2)
        };
        var json = JsonSerializer.Serialize(mappings, s_json);
        var (client, _) = CreateClient(responseBody: json);

        var result = await client.ListMappingsAsync();

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("map-1", result[0].Id);
        Assert.AreEqual("Active", result[1].State);
    }

    [TestMethod]
    public async Task ListMappingsAsync_SetsAgentIdHeader()
    {
        var (client, handler) = CreateClient(responseBody: "[]");

        await client.ListMappingsAsync();

        Assert.IsTrue(handler.CapturedRequest!.Headers.Contains("X-Agent-Id"),
            "X-Agent-Id header should be present");
        Assert.AreEqual("test-agent",
            handler.CapturedRequest.Headers.GetValues("X-Agent-Id").Single());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<string> ReadBodyAsync(FakeHandler handler)
    {
        Assert.IsNotNull(handler.CapturedRequest?.Content, "Expected a request body");
        return await handler.CapturedRequest.Content!.ReadAsStringAsync();
    }
}

// ── Fake HttpMessageHandler (single-request) ──────────────────────────────────

internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly string _responseBody;

    public HttpRequestMessage? CapturedRequest { get; private set; }

    public FakeHandler(HttpStatusCode statusCode, string responseBody)
    {
        _statusCode = statusCode;
        _responseBody = responseBody;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CapturedRequest = request;
        var response = new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
        };
        return Task.FromResult(response);
    }
}

// ── Fake HttpMessageHandler (multi-request) ───────────────────────────────────

internal sealed class MultiRequestFakeHandler : HttpMessageHandler
{
    private readonly IReadOnlyList<(HttpStatusCode status, string body)> _responses;
    private int _index;

    public List<HttpRequestMessage> Requests { get; } = [];

    public MultiRequestFakeHandler(IReadOnlyList<(HttpStatusCode status, string body)> responses)
    {
        _responses = responses;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var (status, body) = _index < _responses.Count
            ? _responses[_index++]
            : (HttpStatusCode.InternalServerError, "no more responses");
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        return Task.FromResult(response);
    }
}
