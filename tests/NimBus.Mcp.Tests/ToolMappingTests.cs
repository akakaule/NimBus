#pragma warning disable CA1707, CA2007, CA1307

using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Mcp.Http;
using NimBus.Mcp.Tools;

namespace NimBus.Mcp.Tests;

/// <summary>
/// Verifies that each <see cref="NimBusAgentTools"/> tool method delegates to the
/// correct <see cref="INimBusAgentApi"/> method with correctly mapped arguments,
/// and handles error cases (409 conflict, 400/404 publish errors, null receive) as
/// specified.
/// </summary>
[TestClass]
public class ToolMappingTests
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    // ── 1. discover_topology ─────────────────────────────────────────────────

    [TestMethod]
    public async Task DiscoverTopology_CallsGetCatalogAsync_AndReturnsJson()
    {
        var catalog = new AgentCatalog(
            ["ep1", "ep2"],
            [new EventTypeInfo("order.created", "Order Created", "{}", "desc")]);
        var fake = new FakeApi { CatalogResult = catalog };
        var tools = new NimBusAgentTools(fake);

        var result = await tools.DiscoverTopologyAsync();

        Assert.IsTrue(fake.GetCatalogCalled, "GetCatalogAsync should have been called");
        // Result should be valid JSON containing the endpoints
        using var doc = JsonDocument.Parse(result);
        Assert.IsTrue(doc.RootElement.TryGetProperty("endpoints", out _), "JSON should contain 'endpoints'");
        Assert.IsTrue(doc.RootElement.TryGetProperty("eventTypes", out _), "JSON should contain 'eventTypes'");
    }

    [TestMethod]
    public async Task DiscoverTopology_WhenCatalogNull_ReturnsNoTopologyAvailable()
    {
        var fake = new FakeApi { CatalogResult = null };
        var tools = new NimBusAgentTools(fake);

        var result = await tools.DiscoverTopologyAsync();

        Assert.IsTrue(fake.GetCatalogCalled, "GetCatalogAsync should have been called");
        Assert.AreEqual("no topology available", result);
    }

    // ── 2. define_event_type ─────────────────────────────────────────────────

    [TestMethod]
    public async Task DefineEventType_BuildsCorrectRequest_AndReturnsJson()
    {
        var returnedInfo = new EventTypeInfo("order.created", "Order Created", "{}", "desc");
        var fake = new FakeApi { DefineEventTypeResult = returnedInfo };
        var tools = new NimBusAgentTools(fake);

        var result = await tools.DefineEventTypeAsync(
            eventTypeId: "order.created",
            jsonSchema: "{\"type\":\"object\"}",
            name: "Order Created",
            description: "An order was created",
            sessionKeyPath: "$.orderId");

        Assert.IsNotNull(fake.CapturedDefineRequest, "DefineEventTypeAsync should have been called");
        Assert.AreEqual("order.created", fake.CapturedDefineRequest!.EventTypeId);
        Assert.AreEqual("{\"type\":\"object\"}", fake.CapturedDefineRequest.JsonSchema);
        Assert.AreEqual("Order Created", fake.CapturedDefineRequest.Name);
        Assert.AreEqual("An order was created", fake.CapturedDefineRequest.Description);
        Assert.AreEqual("$.orderId", fake.CapturedDefineRequest.SessionKeyPath);

        // Result should be valid JSON
        using var doc = JsonDocument.Parse(result);
        Assert.IsTrue(doc.RootElement.TryGetProperty("eventTypeId", out _));
    }

    [TestMethod]
    public async Task DefineEventType_409_ReturnsMeaningfulConflictMessage()
    {
        var fake = new FakeApi
        {
            DefineEventTypeException = new NimBusApiException(409, "Schema mismatch: existing schema differs")
        };
        var tools = new NimBusAgentTools(fake);

        // Should NOT throw — must return a conflict message
        var result = await tools.DefineEventTypeAsync(
            eventTypeId: "order.created",
            jsonSchema: "{\"type\":\"object\"}");

        StringAssert.Contains(result, "conflict", "Result should mention conflict");
        StringAssert.Contains(result, "order.created", "Result should mention the event type id");
        StringAssert.Contains(result, "Schema mismatch", "Result should include the server error detail");
    }

    [TestMethod]
    public async Task DefineEventType_OptionalParamsDefaultToNull_WhenNotProvided()
    {
        var fake = new FakeApi { DefineEventTypeResult = new EventTypeInfo("t", null, null, null) };
        var tools = new NimBusAgentTools(fake);

        await tools.DefineEventTypeAsync("t", "{}");

        Assert.IsNotNull(fake.CapturedDefineRequest);
        Assert.IsNull(fake.CapturedDefineRequest!.Name);
        Assert.IsNull(fake.CapturedDefineRequest.Description);
        Assert.IsNull(fake.CapturedDefineRequest.SessionKeyPath);
    }

    // ── 3. subscribe ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Subscribe_CallsSubscribeAsync_WithCorrectEventTypeId()
    {
        var fake = new FakeApi();
        var tools = new NimBusAgentTools(fake);

        var result = await tools.SubscribeAsync("order.created");

        Assert.IsTrue(fake.SubscribeCalled, "SubscribeAsync should have been called");
        Assert.AreEqual("order.created", fake.CapturedSubscribeRequest!.EventTypeId);
        Assert.AreEqual("subscribed", result);
    }

    // ── 4. receive_messages ──────────────────────────────────────────────────

    [TestMethod]
    public async Task ReceiveMessages_WhenNull_ReturnsNoMessageAvailable()
    {
        var fake = new FakeApi { ReceiveResult = null };
        var tools = new NimBusAgentTools(fake);

        var result = await tools.ReceiveMessagesAsync();

        Assert.AreEqual("no message available", result);
    }

    [TestMethod]
    public async Task ReceiveMessages_WhenMessageAvailable_ReturnsJson()
    {
        var msg = new AgentReceivedMessage(
            "order.created",
            "{\"orderId\":42}",
            new HandoffCoordinates("e1", "s1", "m1", "order.created", "c1", "o1"));
        var fake = new FakeApi { ReceiveResult = msg };
        var tools = new NimBusAgentTools(fake);

        var result = await tools.ReceiveMessagesAsync();

        // Should be valid JSON with the message content
        using var doc = JsonDocument.Parse(result);
        Assert.IsTrue(doc.RootElement.TryGetProperty("eventTypeId", out _));
        Assert.IsTrue(doc.RootElement.TryGetProperty("coordinates", out _));
    }

    [TestMethod]
    public async Task ReceiveMessages_PassesEventTypeIdAndWaitSeconds()
    {
        var fake = new FakeApi { ReceiveResult = null };
        var tools = new NimBusAgentTools(fake);

        await tools.ReceiveMessagesAsync(eventTypeId: "order.created", waitSeconds: 15);

        Assert.AreEqual("order.created", fake.CapturedReceiveEventTypeId);
        Assert.AreEqual(15, fake.CapturedReceiveWaitSeconds);
    }

    // ── 5. publish_event ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task PublishEvent_CallsPublishAsync_WithCorrectArgs_AndReturnsPublished()
    {
        var fake = new FakeApi();
        var tools = new NimBusAgentTools(fake);

        var result = await tools.PublishEventAsync(
            eventTypeId: "order.created",
            payload: "{\"orderId\":42}",
            sessionId: "session-42");

        Assert.IsTrue(fake.PublishCalled, "PublishAsync should have been called");
        Assert.AreEqual("order.created", fake.CapturedPublishRequest!.EventTypeId);
        Assert.AreEqual("{\"orderId\":42}", fake.CapturedPublishRequest.Payload);
        Assert.AreEqual("session-42", fake.CapturedPublishRequest.SessionId);
        Assert.AreEqual("published", result);
    }

    [TestMethod]
    public async Task PublishEvent_400_ReturnsErrorMessage()
    {
        var fake = new FakeApi
        {
            PublishException = new NimBusApiException(400, "Payload does not match schema")
        };
        var tools = new NimBusAgentTools(fake);

        // Should NOT throw — must surface the error so the LLM can fix the payload
        var result = await tools.PublishEventAsync("order.created", "{\"invalid\":true}");

        StringAssert.Contains(result, "400", "Result should include the status code");
        StringAssert.Contains(result, "Payload does not match schema", "Result should include the server error detail");
    }

    [TestMethod]
    public async Task PublishEvent_404_ReturnsErrorMessage()
    {
        var fake = new FakeApi
        {
            PublishException = new NimBusApiException(404, "Event type 'unknown.event' not found")
        };
        var tools = new NimBusAgentTools(fake);

        var result = await tools.PublishEventAsync("unknown.event", "{}");

        StringAssert.Contains(result, "404");
        StringAssert.Contains(result, "not found");
    }

    // ── 6. settle_message ────────────────────────────────────────────────────

    [TestMethod]
    public async Task SettleMessage_BuildsCorrectCoordinatesAndRequest_AndReturnsSettled()
    {
        var fake = new FakeApi();
        var tools = new NimBusAgentTools(fake);

        var result = await tools.SettleMessageAsync(
            eventId: "evt-1",
            sessionId: "sess-1",
            messageId: "msg-1",
            eventTypeId: "order.created",
            correlationId: "corr-1",
            originatingMessageId: "orig-1",
            outcome: "complete",
            result: "{\"processed\":true}",
            errorText: null,
            errorType: null);

        Assert.IsTrue(fake.SettleCalled, "SettleAsync should have been called");
        var req = fake.CapturedSettleRequest!;
        // Verify coordinates
        Assert.AreEqual("evt-1", req.Coordinates.EventId);
        Assert.AreEqual("sess-1", req.Coordinates.SessionId);
        Assert.AreEqual("msg-1", req.Coordinates.MessageId);
        Assert.AreEqual("order.created", req.Coordinates.EventTypeId);
        Assert.AreEqual("corr-1", req.Coordinates.CorrelationId);
        Assert.AreEqual("orig-1", req.Coordinates.OriginatingMessageId);
        // Verify outcome + result
        Assert.AreEqual("complete", req.Outcome);
        Assert.AreEqual("{\"processed\":true}", req.Result);
        Assert.IsNull(req.ErrorText);
        Assert.IsNull(req.ErrorType);
        Assert.AreEqual("settled", result);
    }

    [TestMethod]
    public async Task SettleMessage_FailOutcome_MapsErrorTextAndType()
    {
        var fake = new FakeApi();
        var tools = new NimBusAgentTools(fake);

        await tools.SettleMessageAsync(
            eventId: "e1", sessionId: "s1", messageId: "m1",
            eventTypeId: "order.created", correlationId: "c1", originatingMessageId: "o1",
            outcome: "fail",
            result: null,
            errorText: "Processing failed",
            errorType: "ProcessingError");

        var req = fake.CapturedSettleRequest!;
        Assert.AreEqual("fail", req.Outcome);
        Assert.IsNull(req.Result);
        Assert.AreEqual("Processing failed", req.ErrorText);
        Assert.AreEqual("ProcessingError", req.ErrorType);
    }

    // ── 7. search_failures ───────────────────────────────────────────────────

    [TestMethod]
    public async Task SearchFailures_CallsSearchFailuresAsync_AndReturnsResult()
    {
        var expectedJson = "{\"messages\":[],\"audits\":[]}";
        var fake = new FakeApi { SearchResult = expectedJson };
        var tools = new NimBusAgentTools(fake);

        var result = await tools.SearchFailuresAsync("order.created");

        Assert.IsTrue(fake.SearchCalled, "SearchFailuresAsync should have been called");
        Assert.AreEqual("order.created", fake.CapturedSearchQuery);
        Assert.AreEqual(expectedJson, result);
    }

    // ── 8. propose_mapping ────────────────────────────────────────────────────

    [TestMethod]
    public async Task ProposeMapping_CallsProposeMappingAsync_WithCorrectArgs_AndReturnsJson()
    {
        var mappingInfo = new MappingInfo(
            "map-1",
            "marketing.lead.created.v1",
            "erp.customer.upsert.v1",
            "$$.name",
            "Initial mapping",
            "Draft",
            1);
        var fake = new FakeApi { ProposeMappingResult = mappingInfo };
        var tools = new NimBusAgentTools(fake);

        var result = await tools.ProposeMappingAsync(
            sourceEventTypeId: "marketing.lead.created.v1",
            targetEventTypeId: "erp.customer.upsert.v1",
            transform: "$$.name",
            sourceSchemaHash: "abc123",
            rationale: "Initial mapping");

        Assert.IsNotNull(fake.CapturedProposeMappingRequest, "ProposeMappingAsync should have been called");
        Assert.AreEqual("marketing.lead.created.v1", fake.CapturedProposeMappingRequest!.SourceEventTypeId);
        Assert.AreEqual("erp.customer.upsert.v1", fake.CapturedProposeMappingRequest.TargetEventTypeId);
        Assert.AreEqual("$$.name", fake.CapturedProposeMappingRequest.Transform);
        Assert.AreEqual("abc123", fake.CapturedProposeMappingRequest.SourceSchemaHash);
        Assert.AreEqual("Initial mapping", fake.CapturedProposeMappingRequest.Rationale);

        using var doc = JsonDocument.Parse(result);
        Assert.IsTrue(doc.RootElement.TryGetProperty("id", out _), "JSON should contain 'id'");
        Assert.IsTrue(doc.RootElement.TryGetProperty("state", out _), "JSON should contain 'state'");
    }

    [TestMethod]
    public async Task ProposeMapping_404_ReturnsFriendlyErrorMessage()
    {
        var fake = new FakeApi
        {
            ProposeMappingException = new NimBusApiException(404, "Source event type not found")
        };
        var tools = new NimBusAgentTools(fake);

        // Should NOT throw — must return a friendly error
        var result = await tools.ProposeMappingAsync(
            sourceEventTypeId: "unknown.type",
            targetEventTypeId: "erp.customer.upsert.v1",
            transform: "$$",
            sourceSchemaHash: "abc123");

        StringAssert.Contains(result, "404", "Result should include the status code");
        StringAssert.Contains(result, "Source event type not found", "Result should include the server error detail");
        StringAssert.Contains(result, "define_event_type", "Result should guide the agent to register the event type first");
    }

    [TestMethod]
    public async Task ProposeMapping_OptionalRationaleDefaultsToNull()
    {
        var fake = new FakeApi { ProposeMappingResult = new MappingInfo("m1", "src", "tgt", "$$", null, "Draft", 1) };
        var tools = new NimBusAgentTools(fake);

        await tools.ProposeMappingAsync("src", "tgt", "$$", "hash123");

        Assert.IsNull(fake.CapturedProposeMappingRequest!.Rationale);
    }

    // ── 9. list_mappings ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task ListMappings_CallsListMappingsAsync_AndReturnsJson()
    {
        var mappings = new[]
        {
            new MappingInfo("map-1", "src.event.v1", "tgt.event.v1", "$$", null, "Draft", 1),
            new MappingInfo("map-2", "a.event.v1", "b.event.v1", "$$.id", "reason", "Active", 2)
        };
        var fake = new FakeApi { ListMappingsResult = mappings };
        var tools = new NimBusAgentTools(fake);

        var result = await tools.ListMappingsAsync();

        Assert.IsTrue(fake.ListMappingsCalled, "ListMappingsAsync should have been called");
        using var doc = JsonDocument.Parse(result);
        Assert.AreEqual(JsonValueKind.Array, doc.RootElement.ValueKind, "Result should be a JSON array");
        Assert.AreEqual(2, doc.RootElement.GetArrayLength());
    }

    [TestMethod]
    public async Task ListMappings_WhenEmpty_ReturnsEmptyJsonArray()
    {
        var fake = new FakeApi { ListMappingsResult = [] };
        var tools = new NimBusAgentTools(fake);

        var result = await tools.ListMappingsAsync();

        Assert.IsTrue(fake.ListMappingsCalled);
        using var doc = JsonDocument.Parse(result);
        Assert.AreEqual(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.AreEqual(0, doc.RootElement.GetArrayLength());
    }
}

// ── Fake INimBusAgentApi ──────────────────────────────────────────────────────

/// <summary>
/// Captures calls and args; returns canned DTOs configured before each test.
/// </summary>
internal sealed class FakeApi : INimBusAgentApi
{
    // ── configure per test ──────────────────────────────────────────────────

    public AgentCatalog? CatalogResult { get; set; }
    public EventTypeInfo? DefineEventTypeResult { get; set; }
    public NimBusApiException? DefineEventTypeException { get; set; }
    public AgentReceivedMessage? ReceiveResult { get; set; }
    public NimBusApiException? PublishException { get; set; }
    public string SearchResult { get; set; } = "{}";
    public MappingInfo? ProposeMappingResult { get; set; }
    public NimBusApiException? ProposeMappingException { get; set; }
    public IReadOnlyList<MappingInfo> ListMappingsResult { get; set; } = [];

    // ── captured args ───────────────────────────────────────────────────────

    public bool GetCatalogCalled { get; private set; }
    public DefineEventTypeRequest? CapturedDefineRequest { get; private set; }
    public bool SubscribeCalled { get; private set; }
    public AgentSubscribeRequest? CapturedSubscribeRequest { get; private set; }
    public string? CapturedReceiveEventTypeId { get; private set; }
    public int? CapturedReceiveWaitSeconds { get; private set; }
    public bool PublishCalled { get; private set; }
    public AgentPublishRequest? CapturedPublishRequest { get; private set; }
    public bool SettleCalled { get; private set; }
    public AgentSettleRequest? CapturedSettleRequest { get; private set; }
    public bool SearchCalled { get; private set; }
    public string? CapturedSearchQuery { get; private set; }
    public ProposeMappingRequest? CapturedProposeMappingRequest { get; private set; }
    public bool ListMappingsCalled { get; private set; }

    // ── INimBusAgentApi ─────────────────────────────────────────────────────

    public Task<AgentCatalog?> GetCatalogAsync(CancellationToken ct = default)
    {
        GetCatalogCalled = true;
        return Task.FromResult(CatalogResult);
    }

    public Task<EventTypeInfo?> DefineEventTypeAsync(DefineEventTypeRequest req, CancellationToken ct = default)
    {
        CapturedDefineRequest = req;
        if (DefineEventTypeException is not null)
        {
            throw DefineEventTypeException;
        }

        return Task.FromResult(DefineEventTypeResult);
    }

    public Task SubscribeAsync(AgentSubscribeRequest req, CancellationToken ct = default)
    {
        SubscribeCalled = true;
        CapturedSubscribeRequest = req;
        return Task.CompletedTask;
    }

    public Task<AgentReceivedMessage?> ReceiveAsync(string? eventTypeId, int? waitSeconds, CancellationToken ct = default)
    {
        CapturedReceiveEventTypeId = eventTypeId;
        CapturedReceiveWaitSeconds = waitSeconds;
        return Task.FromResult(ReceiveResult);
    }

    public Task PublishAsync(AgentPublishRequest req, CancellationToken ct = default)
    {
        PublishCalled = true;
        CapturedPublishRequest = req;
        if (PublishException is not null)
        {
            throw PublishException;
        }

        return Task.CompletedTask;
    }

    public Task SettleAsync(AgentSettleRequest req, CancellationToken ct = default)
    {
        SettleCalled = true;
        CapturedSettleRequest = req;
        return Task.CompletedTask;
    }

    public Task<string> SearchFailuresAsync(string query, CancellationToken ct = default)
    {
        SearchCalled = true;
        CapturedSearchQuery = query;
        return Task.FromResult(SearchResult);
    }

    public Task<MappingInfo?> ProposeMappingAsync(ProposeMappingRequest req, CancellationToken ct = default)
    {
        CapturedProposeMappingRequest = req;
        if (ProposeMappingException is not null)
        {
            throw ProposeMappingException;
        }

        return Task.FromResult(ProposeMappingResult);
    }

    public Task<IReadOnlyList<MappingInfo>> ListMappingsAsync(CancellationToken ct = default)
    {
        ListMappingsCalled = true;
        return Task.FromResult(ListMappingsResult);
    }
}
