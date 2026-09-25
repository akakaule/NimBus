# NimBus operator MCP server

The management WebApp can serve a **Model Context Protocol (MCP)** endpoint at `/mcp`. It lets an
AI agent (Claude Code, Claude Desktop or any MCP client) look at endpoint health, find and inspect
messages, search across endpoints, read metrics and read stored failure classifications, under the
same authorization, redaction and audit as the Web UI.

- **Transport:** Streamable HTTP, stateless, protocol revisions up to 2026-07-28
  (`ModelContextProtocol.AspNetCore`).
- **Access:** read-only in this release. There are no recovery or administrative tools.
- **Design:** [Spec 035](spec/035-mcp-operator-access/spec.md).

The endpoint is off unless it is enabled. It replaces the earlier stdio server `NimBus.Mcp`
(see [Migrating from NimBus.Mcp](#migrating-from-nimbusmcp)).

## Tools

| Tool | Returns | Needs |
| --- | --- | --- |
| `nimbus_get_capabilities` | Environment, caller, readable endpoints, limits and features. Call it first | Signed in |
| `nimbus_list_endpoints` | Readable endpoints with the event types each produces and consumes | Endpoint Reader |
| `nimbus_get_overview` | Counts by status, oldest failure and Monitor acknowledgements across readable endpoints | Endpoint Reader |
| `nimbus_get_endpoint` | The same for one endpoint | Endpoint Reader |
| `nimbus_find_messages` | Tracked messages on one endpoint, filtered by status, event type, session, event id and time | Endpoint Reader |
| `nimbus_get_message` | Status, latest attempt, latest error and Web UI link; the payload only with `includePayload=true` | Endpoint Reader; payloads also need PiiReader (and, with Entra, the `nimbus.payload.read` scope) |
| `nimbus_get_message_history` | Processing attempts and log entries of one message | Endpoint Reader |
| `nimbus_get_session` | Pending and deferred events of one session | Endpoint Reader |
| `nimbus_search_messages` | Processing messages across all endpoints | Site Reader |
| `nimbus_get_metrics` | Throughput, latency or failure groups for 1h to 30d | Site Reader |
| `nimbus_get_classification` | The stored AI classification of a failed attempt, advisory only | Endpoint Reader; classification enabled |

Results never contain stack traces. Error and log text is cut to 2,000 characters and is untrusted
data from the failing handler. Timestamps are UTC. Errors start with a code in brackets, for example
`[EndpointNotFound]` or `[PermissionDenied]`. A resource the caller cannot read is reported exactly
like one that does not exist.

## Local development (Aspire)

No setup is needed. The NimBus Aspire AppHost (`src/NimBus.AppHost`) sets
`NimBus__Mcp__EnableForLocalDevelopment=true`, and the
WebApp serves `/mcp` whenever its local-dev bypass is on (Development and
`EnableLocalDevAuthentication=true` in `src/NimBus.WebApp/appsettings.Development.json`). No sign-in
is involved; every call runs as the "Local Developer" user, with the same role and PII checks as the
Web UI. With the bypass off, `/mcp` is not served.

Point the client at the WebApp's HTTPS URL from the Aspire dashboard (by default
`https://localhost:18443`) plus `/mcp`. For example, an `.mcp.json` entry:

```json
{
  "mcpServers": {
    "nimbus": { "type": "http", "url": "https://localhost:18443/mcp" }
  }
}
```

In this mode the endpoint accepts loopback connections only, and refuses browser requests whose
`Origin` is not a localhost address. This protects against DNS rebinding.

## Azure (Entra ID)

In Azure the endpoint requires an Entra access token issued for its own app registration. It does
not use the WebApp's sign-in registration, cookies or the `"Az"` bearer scheme.

### 1. Register the MCP resource

In the tenant that signs in your operators:

1. Create an app registration, for example `NimBus MCP (dev)`, single tenant. Note its
   **Application (client) ID**.
2. **Expose an API:** keep the default Application ID URI (`api://<client-id>`) or set your own.
   Add two delegated scopes:
   - `nimbus.observe`: use the read-only tools.
   - `nimbus.payload.read`: see raw event payloads. It only adds to the PiiReader role, and never
     replaces it.
3. **App roles** (optional, for unattended workloads): add `Nimbus.Observe` for applications. No
   app role grants payload access.
4. **Token configuration** (optional): add the `groups` claim if your NimBus role grants use Entra
   groups. Grants keyed by object id or email work without it.
5. Approve the MCP clients you allow. Register them, or pre-authorize their client ids, for
   `nimbus.observe`, and add `nimbus.payload.read` only where needed.

The endpoint accepts v2.0 tokens (issuer `https://login.microsoftonline.com/<tenant>/v2.0`,
audience the client id) and v1.0 tokens (issuer `https://sts.windows.net/<tenant>/`, audience the
Application ID URI).

### 2. Configure the WebApp

Set these app settings on the management WebApp:

| Setting | Value |
| --- | --- |
| `NimBus__Mcp__Enabled` | `true` |
| `NimBus__Mcp__Entra__TenantId` | Tenant id |
| `NimBus__Mcp__Entra__ClientId` | The MCP app registration's client id |
| `NimBus__Mcp__Entra__ApplicationIdUri` | Only if you changed it from `api://<client-id>` |
| `NimBus__Mcp__Entra__Instance` | Only for a sovereign cloud; default `https://login.microsoftonline.com/` |
| `NimBus__Mcp__AllowedOrigins__0` | Only for browser-based MCP clients: their origin |

```bash
az webapp config appsettings set --resource-group <resource-group> --name <webapp-name> --settings NimBus__Mcp__Enabled=true NimBus__Mcp__Entra__TenantId=<tenant-id> NimBus__Mcp__Entra__ClientId=<client-id>
```

`nb deploy infra` keeps every `NimBus__Mcp__*` setting across redeploys. If `NimBus__Mcp__Enabled`
is `true` but the tenant or client id is missing, the WebApp fails to start and names the missing
keys, rather than serving an unauthenticated endpoint.

### 3. Verify

- `GET https://<webapp>/.well-known/oauth-protected-resource/mcp` returns the resource metadata:
  the authorization server and the two scopes.
- `POST https://<webapp>/mcp` without a token returns `401` with a `WWW-Authenticate` header that
  points at that metadata.
- An MCP client that signs in with the `nimbus.observe` scope can call `nimbus_get_capabilities`.

In [private networking mode](spec/034-private-networking/spec.md), only clients inside the network
can reach `/mcp`. Cloud-hosted agents need a path through the Application Gateway.

## Limits

`/mcp` has its own rate-limit policy, `nimbus-mcp`: 60 requests per 60 seconds, per tenant,
client application and user. See [rate limiting](rate-limiting.md).

## Migrating from NimBus.Mcp

`NimBus.Mcp` was a local stdio process that called `/api/agent/*` and identified itself with an
`X-Agent-Id` header. It has been removed.

- **Client configuration:** replace the `mcpServers` entry that ran `dotnet run --project
  src/NimBus.Mcp/...` with the `/mcp` URL above, and drop `NIMBUS_API_BASEURL` and
  `NIMBUS_AGENT_ID`.
- **Replaced tools:** `discover_topology` is now `nimbus_list_endpoints`. `search_failures` is now
  `nimbus_find_messages`, `nimbus_search_messages` and `nimbus_get_message_history`.
- **Bus participation:** `define_event_type`, `subscribe`, `receive_messages`, `publish_event` and
  `settle_message` are not operator tools. Agents that take part on the bus use the `NimBus.Agents`
  REST SDK against `/api/agent/*`.
