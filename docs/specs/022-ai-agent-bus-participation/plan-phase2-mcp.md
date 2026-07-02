# Spec 022 — Phase 2 Implementation Plan: `NimBus.Mcp` (MCP server)

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development (or executing-plans). Steps use checkbox (`- [ ]`).

**Goal:** Ship `src/NimBus.Mcp/` — a standalone, **thin** stdio Model Context Protocol server whose tools are 1:1 wrappers over the existing `/api/agent/*` REST endpoints (Phase 1), so any MCP client (Claude Code/Desktop) can drive NimBus with no business logic in the adapter.

**Architecture:** `Host.CreateApplicationBuilder` → `AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()`. Tools delegate to a typed `HttpClient` (`INimBusAgentApi`) that calls the WebApp over HTTP with an `X-Agent-Id` header. DTOs are hand-written `record`s (no dependency on the WebApp/SPA). Config (base URL, agent id) from env vars.

**Tech Stack:** .NET 10, `ModelContextProtocol` NuGet (stdio + DI + attribute tools), `Microsoft.Extensions.Hosting`, `System.Net.Http.Json`, MSTest.

> **Verify against the pinned SDK** (`ModelContextProtocol`, ~v1.0.0 confirmed present transitively via Aspire 13.3.5; check nuget.org for newer stable). Re-confirm: builder methods (`WithStdioServerTransport`, `WithToolsFromAssembly`), the `[McpServerToolType]`/`[McpServerTool]`/`[Description]` attributes (namespace `ModelContextProtocol.Server`), and the **DI-injection vs LLM-argument parameter-binding rule** (registered-service params are injected; primitive params become the tool's input schema). Central Package Management is OFF — pin versions inline in the csproj.

---

## Spec-table correction (carry into the tools)

The spec's MCP→REST table lists `search_failures` as `GET /api/messages/search` + `/api/audits/search`. The **real** endpoints are **POST** with bodies (`MessageSearchRequest` / `AuditSearchRequest`). Implement them as POST.

---

## File structure
```
src/NimBus.Mcp/
  NimBus.Mcp.csproj            # Microsoft.NET.Sdk, OutputType=Exe; refs ModelContextProtocol + M.E.Hosting
  Program.cs                   # stdio host; logs → stderr (stdout is the JSON-RPC channel)
  Configuration/NimBusMcpOptions.cs
  Http/INimBusAgentApi.cs      # one method per tool (the test seam)
  Http/NimBusAgentApiClient.cs # typed HttpClient impl
  Http/Contracts.cs            # minimal request/response records (System.Text.Json)
  Tools/NimBusAgentTools.cs    # [McpServerToolType] — 7 tools, each a thin delegate
  appsettings.json             # optional fallback for base url / agent id
tests/NimBus.Mcp.Tests/
  NimBus.Mcp.Tests.csproj      # MSTest; refs NimBus.Mcp
  NimBusAgentApiClientTests.cs # HTTP-shape via fake HttpMessageHandler
  ToolMappingTests.cs          # tool→client mapping via fake INimBusAgentApi
docs/mcp-server.md             # usage + tool table + Claude Desktop/Code config
```

---

### Task 1: Scaffold the project + minimal stdio host

**Files:** Create `src/NimBus.Mcp/NimBus.Mcp.csproj`, `src/NimBus.Mcp/Program.cs`; modify `src/NimBus.sln`.

- [ ] **Step 1: csproj** (follow the lean `AspirePubSub.Provisioner` template — `TargetFramework`/`Nullable`/`ImplicitUsings` come from `Directory.Build.props`, don't re-declare):
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ModelContextProtocol" Version="1.0.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.0" />
  </ItemGroup>
</Project>
```
(Confirm the resolved `ModelContextProtocol` + `Microsoft.Extensions.Hosting` versions; match what the repo already resolves.)

- [ ] **Step 2: minimal Program.cs** — boots the stdio server with zero tools; logs to stderr:
```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace); // stdout = JSON-RPC

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
```

- [ ] **Step 3: add to `src/NimBus.sln`** — a `Project(...)` line `src\NimBus.Mcp\NimBus.Mcp.csproj` + a matching `GlobalSection(ProjectConfigurationPlatforms)` Debug/Release × Any CPU block (mirror an existing `src/` project's GUID block; use a fresh GUID).

- [ ] **Step 4: build** — `dotnet build src/NimBus.Mcp/NimBus.Mcp.csproj` → SUCCESS (SDK resolves). Then `dotnet build src/NimBus.sln` → 0 errors.

- [ ] **Step 5: commit** — `feat(mcp): scaffold NimBus.Mcp stdio server project (spec 022)`

---

### Task 2: Config (`NimBusMcpOptions`)

**Files:** Create `src/NimBus.Mcp/Configuration/NimBusMcpOptions.cs`; modify `Program.cs`, `appsettings.json`.

- [ ] **Step 1:** options bound env-first (`NIMBUS_API_BASEURL`, `NIMBUS_AGENT_ID`), appsettings fallback:
```csharp
public sealed class NimBusMcpOptions
{
    public string BaseUrl { get; set; } = "http://localhost:5000";
    public string AgentId { get; set; } = "demo-agent";
}
```
Bind in Program.cs from `builder.Configuration` (env vars override appsettings). Validate `BaseUrl` is an absolute URI at startup.

- [ ] **Step 2: build; commit** — `feat(mcp): config options from env/appsettings (spec 022)`

---

### Task 3: Typed HTTP client + DTOs (TDD)

**Files:** Create `Http/INimBusAgentApi.cs`, `Http/NimBusAgentApiClient.cs`, `Http/Contracts.cs`; test `tests/NimBus.Mcp.Tests/NimBusAgentApiClientTests.cs`.

- [ ] **Step 1: DTO records** (mirror `api-spec.yaml` agent schemas; `payload`/`jsonSchema`/`result` are `string`):
`AgentCatalog(string[] Endpoints, EventTypeInfo[] EventTypes)`, `EventTypeInfo(string EventTypeId, string? Name, string? JsonSchema, string? Description)`, `DefineEventTypeRequest(string EventTypeId, string JsonSchema, string? Name, string? Description, string? SessionKeyPath)`, `AgentSubscribeRequest(string EventTypeId)`, `HandoffCoordinates(string EventId, string SessionId, string MessageId, string EventTypeId, string CorrelationId, string OriginatingMessageId)`, `AgentReceivedMessage(string EventTypeId, string Payload, HandoffCoordinates Coordinates)`, `AgentPublishRequest(string EventTypeId, string Payload, string? SessionId)`, `AgentSettleRequest(HandoffCoordinates Coordinates, string Outcome, string? Result, string? ErrorText, string? ErrorType)`. Use `System.Text.Json` with camelCase (`JsonSerializerOptions(JsonSerializerDefaults.Web)`).

- [ ] **Step 2: interface** `INimBusAgentApi`:
```csharp
Task<AgentCatalog?> GetCatalogAsync(CancellationToken ct = default);
Task<EventTypeInfo?> DefineEventTypeAsync(DefineEventTypeRequest req, CancellationToken ct = default); // 409 -> throw a typed SchemaConflict
Task SubscribeAsync(AgentSubscribeRequest req, CancellationToken ct = default);
Task<AgentReceivedMessage?> ReceiveAsync(string? eventTypeId, int? waitSeconds, CancellationToken ct = default); // 204 -> null
Task PublishAsync(AgentPublishRequest req, CancellationToken ct = default); // 400/404 -> typed error
Task SettleAsync(AgentSettleRequest req, CancellationToken ct = default);
Task<string> SearchFailuresAsync(string query, CancellationToken ct = default); // POST messages/search + audits/search, combined
```

- [ ] **Step 3: failing HTTP-shape tests** — construct `NimBusAgentApiClient` over an `HttpClient` backed by a **fake `HttpMessageHandler`** capturing the outgoing request. For each method assert: HTTP verb, path, `X-Agent-Id` header, query string (`eventTypeId`/`waitSeconds`), JSON body. Canned responses must cover **204 → null** (receive-empty), **409** (define-conflict → typed exception), **400/404** (publish). Run: expect FAIL (no impl).

- [ ] **Step 4: implement `NimBusAgentApiClient`** — `AddHttpClient<NimBusAgentApiClient>` registers it; default `X-Agent-Id` header from options; each method maps verb/path/body per the table; receive maps 204→null; non-success (400/404/409) → throw a small typed `NimBusApiException(int status, string body)` (so tools surface actionable errors to the LLM). Register `INimBusAgentApi` → `NimBusAgentApiClient` and the typed client in DI in Program.cs.

- [ ] **Step 5: run tests → PASS. Build solution. Commit** — `feat(mcp): typed agent REST client + DTOs (spec 022)`

---

### Task 4: MCP tools (1:1)

**Files:** Create `Tools/NimBusAgentTools.cs`; test `tests/NimBus.Mcp.Tests/ToolMappingTests.cs`.

- [ ] **Step 1: tools** — one `[McpServerToolType]` class; seven `[McpServerTool]` methods, each a thin delegate to `INimBusAgentApi` (injected), with LLM-facing `[Description]`s and primitive input params:
`discover_topology` → GetCatalogAsync; `define_event_type(eventTypeId, jsonSchema, name?, description?, sessionKeyPath?)` → DefineEventTypeAsync; `subscribe(eventTypeId)` → SubscribeAsync; `receive_messages(eventTypeId?, waitSeconds?)` → ReceiveAsync (return a JSON string / "no message"); `publish_event(eventTypeId, payload, sessionId?)` → PublishAsync; `settle_message(eventId, sessionId, messageId, eventTypeId, correlationId, originatingMessageId, outcome, result?, errorText?, errorType?)` → SettleAsync; `search_failures(query)` → SearchFailuresAsync. Tools return strings (JSON or a human-readable status). No business logic.

> Verify the DI-vs-LLM param rule: keep `INimBusAgentApi` as an injected param and all other params primitive. If the pinned SDK injects differently, adjust (e.g. resolve the api from an `IServiceProvider` param).

- [ ] **Step 2: failing tool-mapping tests** — inject a **fake `INimBusAgentApi`**, invoke each tool method directly (no JSON-RPC server), assert it calls the correct api method with correctly-mapped args (incl. define-conflict surfacing and receive-null → "no message"). Run: expect FAIL.

- [ ] **Step 3: implement tools → tests PASS.** Build solution. **Commit** — `feat(mcp): 7 agent MCP tools mapped 1:1 to REST (spec 022)`

---

### Task 5: Docs

**Files:** Create `docs/mcp-server.md`.

- [ ] **Step 1:** document the 7 tools + the **corrected** REST mapping table (search = POST), the env-var config (`NIMBUS_API_BASEURL`, `NIMBUS_AGENT_ID`), and a sample Claude Desktop/Code `mcpServers` stdio entry (`command: dotnet`, `args: [run/path-to-dll]`, `env: {...}`). Note the demo-grade auth (X-Agent-Id only; leave a seam for a future `X-Api-Key`).

- [ ] **Step 2: commit** — `docs(mcp): MCP server usage + tool mapping (spec 022)`

---

### Task 6: Full build + verify + (optional) manual MCP smoke

- [ ] **Step 1:** `dotnet build src/NimBus.sln` (Release = warnings-as-errors) → 0 errors; `dotnet test tests/NimBus.Mcp.Tests/` → green.
- [ ] **Step 2 (optional manual):** run the WebApp locally, run `NimBus.Mcp` from an MCP client (or the MCP Inspector), confirm `discover_topology` returns the catalog. Document the result; do not gate CI on it.

---

## Self-review notes
- **Spec coverage:** all 7 tools (decision-#5 "hollow adapter" honored — no logic outside the REST client). MCP-mapping tests satisfy the spec's "MCP mapping" test level. HTTP/SSE transport is optional and deferred (stdio-first).
- **No WebApp dependency** — hand-written DTOs keep the adapter thin; the generated TS client is SPA-only and irrelevant.
- **Verify flags:** SDK version + builder/attribute API + param-binding rule against the pinned `ModelContextProtocol`; correct `search_failures` to POST.
