# NimBus MCP Server (`NimBus.Mcp`)

`NimBus.Mcp` is a standalone **Model Context Protocol (MCP)** server that lets an LLM agent (Claude Desktop, Claude Code, or any MCP client) drive NimBus as a bus participant — discover the topology, define event types, subscribe, receive, publish, and settle — with no NimBus C# written.

It is a **thin adapter**: every tool is a 1:1 wrapper over a NimBus management REST endpoint (`/api/agent/*` plus the two search endpoints). All capability lives in the REST API (the "capability core", spec 022 decision #5); the MCP server contains no business logic. The same REST API also backs the WebApp dashboard and (later) a typed .NET agent SDK.

- **Project:** `src/NimBus.Mcp` · **Transport:** stdio (stdout is the JSON-RPC channel; logs go to stderr) · **SDK:** `ModelContextProtocol` 1.4.0 · **Target:** .NET 10.

## Tools → REST mapping

| MCP tool | REST endpoint | Notes |
|---|---|---|
| `discover_topology` | `GET /api/agent/catalog` | Endpoints + registered agent event-type schemas. Call this first. |
| `define_event_type` | `POST /api/agent/event-types` | Registers a JSON-Schema event type. Idempotent on an identical schema; **`409`** (returned as a readable conflict message) if the id already exists with a *different* schema (schemas are immutable in v1). |
| `subscribe` | `POST /api/agent/subscribe` | Records a pull-based logical subscription to an event type (filters `receive_messages`). |
| `receive_messages` | `GET /api/agent/receive?eventTypeId=&waitSeconds=` | Long-poll pull of the next parked message; returns `"no message available"` on `204`. `waitSeconds` is clamped server-side to 0–60. |
| `publish_event` | `POST /api/agent/publish` | Publishes an event; the payload is validated against the registered schema. **`400`** (schema-invalid) / **`404`** (unknown type) are returned as readable error messages the agent can act on. |
| `settle_message` | `POST /api/agent/settle` | Complete or fail received work (`outcome` = `complete` \| `fail`; `errorText` required on `fail`). Pass the `HandoffCoordinates` returned by `receive_messages` verbatim. |
| `search_failures` | `POST /api/messages/search` + `POST /api/audits/search` | Read-only diagnostics. *(These are POST with request bodies — the spec's draft table listed GET; POST is correct.)* |

## Configuration

The server reads two settings — env vars take precedence over `appsettings.json`:

| Setting | Env var | `appsettings.json` (`NimBus` section) | Default |
|---|---|---|---|
| WebApp base URL | `NIMBUS_API_BASEURL` | `BaseUrl` | `http://localhost:5000` |
| Agent identity | `NIMBUS_AGENT_ID` | `AgentId` | `demo-agent` |

`NIMBUS_AGENT_ID` is sent as the `X-Agent-Id` header on every request — the demo-grade agent identity recorded on the agent's definitions/subscriptions/published events/receives/settles. The base URL is validated as an absolute URI at startup.

> **Auth (demo-grade).** The `/api/agent/*` surface currently enforces no API-key check — identity is the `X-Agent-Id` header only. Production auth (scoped API keys / OAuth, quotas) is a deferred sub-project; a clear seam exists to add an `X-Api-Key` header later.

## Running it from an MCP client

The MCP client launches the server as a stdio subprocess. Example Claude Desktop / Claude Code `mcpServers` entry (point `--project` at the built project, or run the published DLL with `dotnet <path>/NimBus.Mcp.dll`):

```json
{
  "mcpServers": {
    "nimbus": {
      "command": "dotnet",
      "args": ["run", "--project", "src/NimBus.Mcp/NimBus.Mcp.csproj", "-c", "Release"],
      "env": {
        "NIMBUS_API_BASEURL": "http://localhost:28375",
        "NIMBUS_AGENT_ID": "enrichment-agent"
      }
    }
  }
}
```

The NimBus management WebApp must be running and reachable at `NIMBUS_API_BASEURL` (e.g. the CrmErpDemo AppHost exposes it as `nimbus-ops`). A typical agent loop is `discover_topology → (first run) define_event_type → subscribe → receive_messages → publish_event → settle_message`.

## A note on the demo runner

The Phase 3 `EnrichmentAgent` demo drives this exact loop, but calls the REST API **directly** (not through this MCP server) for determinism and reproducibility — the MCP server is the *interactive* hero (an LLM in an MCP client), while the scripted demo keeps the same capabilities behind an `IBusGateway` seam so the MCP path is a swap. See `docs/specs/022-ai-agent-bus-participation/plan-phase3-demo.md`.

## Testing

`tests/NimBus.Mcp.Tests` has two layers, no MCP transport or live WebApp required:
- **HTTP-shape tests** (`NimBusAgentApiClientTests`) — a fake `HttpMessageHandler` asserts each client method's verb, path, `X-Agent-Id` header, query string, and body, including `204 → null` and `400/404/409 → NimBusApiException`.
- **Tool-mapping tests** (`ToolMappingTests`) — a fake `INimBusAgentApi` confirms each tool calls the correct endpoint with correctly-mapped arguments and surfaces conflict/validation errors and empty results as readable strings.
