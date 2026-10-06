# NimBus operator MCP server

The management WebApp can serve a **Model Context Protocol (MCP)** endpoint at `/mcp`. It lets an
AI agent (Claude Code, Claude Desktop or any MCP client) look at endpoint health, find and inspect
messages, search across endpoints, read metrics and failure classifications, and recover single
failed messages, under the same authorization, redaction and audit as the Web UI.

- **Transport:** Streamable HTTP, stateless, protocol revisions up to 2026-07-28
  (`ModelContextProtocol.AspNetCore`).
- **Access:** read tools, plus single-message recovery (resubmit, skip, mark reported, classify)
  for signed-in users. Workloads that sign in as themselves (app-only tokens) can only read. There
  are no bulk, handoff, payload-edit or administrative tools.
- **Design:** [Spec 035](spec/035-mcp-operator-access/spec.md).

The endpoint is off unless it is enabled. It replaces the earlier stdio server `NimBus.Mcp`
(see [Migrating from NimBus.Mcp](#migrating-from-nimbusmcp)).

## Tools

### Read tools

| Tool | Returns | Needs |
| --- | --- | --- |
| `nimbus_get_capabilities` | Environment, caller, readable endpoints, the actions the caller may take (`permittedActions`), limits and features. Call it first | Signed in |
| `nimbus_list_endpoints` | Readable endpoints with the event types each produces and consumes | Endpoint Reader |
| `nimbus_get_overview` | Counts by status, oldest failure and Monitor acknowledgements across readable endpoints | Endpoint Reader |
| `nimbus_get_endpoint` | The same for one endpoint | Endpoint Reader |
| `nimbus_find_messages` | Tracked messages on one endpoint, filtered by status, event type, session, event id and time | Endpoint Reader |
| `nimbus_get_message` | Status, latest attempt, latest error, Web UI link, `messageVersion` and `eligibleActions`; the payload only with `includePayload=true` | Endpoint Reader; payloads also need PiiReader (and, with Entra, the `nimbus.payload.read` scope) |
| `nimbus_get_message_history` | Processing attempts and log entries of one message | Endpoint Reader |
| `nimbus_get_session` | Pending and deferred events of one session | Endpoint Reader |
| `nimbus_search_messages` | Processing messages across all endpoints | Site Reader |
| `nimbus_get_metrics` | Throughput, latency or failure groups for 1h to 30d | Site Reader |
| `nimbus_get_classification` | The stored AI classification of a failed attempt, advisory only | Endpoint Reader; classification enabled |

### Action tools

| Tool | Does | Needs |
| --- | --- | --- |
| `nimbus_prepare_action` | Checks and previews a resubmit or skip of one message without changing anything, and returns an `actionToken` | The action's scope and endpoint Contributor |
| `nimbus_resubmit_message` | Sends the prepared message to its endpoint again with its latest stored payload | `nimbus.resubmit` and endpoint Contributor |
| `nimbus_skip_message` | Skips the prepared message. It is never processed, and later messages waiting in its session may be released | `nimbus.skip` and endpoint Contributor |
| `nimbus_set_message_reported` | Marks a message reported, optionally with a ticket id, or clears the marker. Changes nothing on the bus | `nimbus.annotate` and endpoint Contributor |
| `nimbus_classify_failure` | Requests an AI classification of a failed attempt, or returns the stored one. Advisory only | `nimbus.classify`, endpoint Contributor and classification enabled |

See [Recovering messages](#recovering-messages) for how these run.

Results never contain stack traces. Error and log text is cut to 2,000 characters and is untrusted
data from the failing handler. Timestamps are UTC. Errors start with a code in brackets, for example
`[EndpointNotFound]` or `[PermissionDenied]`. A resource the caller cannot read is reported exactly
like one that does not exist.

## Recovering messages

Resubmit and skip take two steps, so an agent acts only on the state it showed the user:

1. `nimbus_get_message` returns the message's `messageVersion` and the `eligibleActions` the
   caller may take on it now.
2. `nimbus_prepare_action` with `action` (`resubmit` or `skip`), the endpoint, the event id and
   that `messageVersion` checks the scope, the role and the message's state. It returns the impact,
   including how many later messages wait in the session, and an `actionToken`. The token is valid
   for 120 seconds, only for the same user, client application, tenant and environment, and only
   for that action on that version.
3. `nimbus_resubmit_message` or `nimbus_skip_message` with the `actionToken`, a new GUID as
   `idempotencyKey` and a `reason` (1 to 1,000 characters) runs the action.

Each change goes through the same guarded path as the Web UI's buttons:

- **Fresh role check.** The Contributor role is checked again against the access-control lists in
  the store, not the cached copy, so a grant revoked a moment ago is honored.
- **Audit first.** The action is written to the audit log before anything is sent: channel,
  reason, idempotency key, client application, prior status and message version. If the audit
  row cannot be written, nothing runs (`[AuditUnavailable]`). Resubmit, skip and mark reported
  are always audited; Admin → Audit cannot switch them off.
- **Claim, then send.** The message is claimed only while it still has the previewed latest
  attempt. If the Web UI or another agent acted first, the call fails with `[StaleMessage]` and
  nothing is sent. If sending then fails, the claim is undone, a `CommandNotSent` audit row is
  written, and the call fails with `[OutcomeUnknown]`.

Through MCP, resubmit and skip accept Failed, DeadLettered and Unsupported messages. Pending
handoffs, deferred messages and payload edits stay in the Web UI. A successful call returns
`status: Accepted` and `commandSent: true`: the command was sent, not processed. Read the message
again to see the outcome. An expired or foreign token is also `[StaleMessage]`: read the message
again and prepare a new token.

`nimbus_set_message_reported` and `nimbus_classify_failure` run in one step. Mark reported keeps
the Web UI's last-writer-wins behaviour, with the audit written first. Classification is the Web
UI's **Analyze failure** action on Failed and DeadLettered messages; repeating an `idempotencyKey`
returns the same result, and `force=true` classifies again.

The action tools add these error codes: `[StaleMessage]`, `[ActionNotAllowed]` (the message's
state or endpoint does not allow the action), `[RateLimited]`, `[AuditUnavailable]` and
`[OutcomeUnknown]`.

## Local development (Aspire)

With the NimBus Aspire AppHost (`src/NimBus.AppHost`) no setup is needed: it sets
`NimBus__Mcp__EnableForLocalDevelopment=true`, and the
WebApp serves `/mcp` whenever its local-dev bypass is on (Development and
`EnableLocalDevAuthentication=true` in `src/NimBus.WebApp/appsettings.Development.json`). The
CrmErpDemo AppHost does not set it; to use `/mcp` there, set `NimBus:Mcp:EnableForLocalDevelopment`
to `true` yourself, for example in that `appsettings.Development.json`. No sign-in
is involved; every call runs as the "Local Developer" user, with the same role and PII checks as the
Web UI. There are no scopes in this mode, so the action tools are available wherever that user's
role allows them. With the bypass off, `/mcp` is not served.

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
   Add the delegated scopes:
   - `nimbus.observe`: use the read tools. Every call needs it.
   - `nimbus.payload.read`: see raw event payloads. It only adds to the PiiReader role, and never
     replaces it.
   - `nimbus.resubmit`: resubmit failed messages.
   - `nimbus.skip`: skip failed messages.
   - `nimbus.annotate`: mark messages reported.
   - `nimbus.classify`: request AI failure classifications.

   Like `nimbus.payload.read`, the four action scopes only add to the user's NimBus role: each
   action still needs Contributor on the endpoint. Leave out the scopes of actions you do not
   want agents to take.
3. **Add the endpoint URL as a second Application ID URI:** `https://<webapp>/mcp`, exactly as
   clients connect to it. MCP clients send that URL as the OAuth `resource` parameter, and Entra
   refuses a sign-in whose `resource` does not belong to the same app as the requested scopes
   (`AADSTS9010010: The resource parameter provided in the request doesn't match with the
   requested scopes`). Keep `api://<client-id>` as well: the advertised scopes use it.
   ```bash
   az ad app update --id <client-id> --identifier-uris "api://<client-id>" "https://<webapp>/mcp"
   ```
4. **Manifest:** set `api.requestedAccessTokenVersion` to `2`, so access tokens carry the client
   id as their audience whichever identifier the client asked for.
5. **App roles** (optional, for unattended workloads): add `Nimbus.Observe` for applications. No
   app role grants payload access or any action: a token without a delegated `scp` is refused by
   every action tool.
6. **Token configuration** (optional): add the `groups` claim if your NimBus role grants use Entra
   groups. Grants keyed by object id or email work without it.
7. **Register the MCP clients you allow.** Entra does not support dynamic client registration, so
   every MCP client needs a client id of its own. For a desktop client such as Claude Code, create
   a second single-tenant app registration with a **Mobile and desktop** redirect URI on the
   client's loopback callback (for Claude Code `http://localhost:<port>/callback`, with the port
   you pass as `--callback-port`) and **Allow public client flows** turned on; it needs no secret.
   Then, on the MCP resource, pre-authorize that client id for `nimbus.observe`, and for
   `nimbus.payload.read` and the action scopes only where needed, or grant consent for it.

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
| `NimBus__Mcp__Entra__ApplicationIdUri` | Only if you changed it from `api://<client-id>`. The scopes' identifier, not the endpoint URL from step 1.3 |
| `NimBus__Mcp__Entra__Instance` | Only for a sovereign cloud; default `https://login.microsoftonline.com/` |
| `NimBus__Mcp__AllowedOrigins__0` | Only for browser-based MCP clients: their origin |

```bash
az webapp config appsettings set --resource-group <resource-group> --name <webapp-name> --settings NimBus__Mcp__Enabled=true NimBus__Mcp__Entra__TenantId=<tenant-id> NimBus__Mcp__Entra__ClientId=<client-id>
```

`nb infra apply` keeps every `NimBus__Mcp__*` setting across redeploys. If `NimBus__Mcp__Enabled`
is `true` but the tenant or client id is missing, the WebApp fails to start and names the missing
keys, rather than serving an unauthenticated endpoint.

### 3. Verify

- `GET https://<webapp>/.well-known/oauth-protected-resource/mcp` returns the resource metadata:
  the authorization server and the six scopes.
- `POST https://<webapp>/mcp` without a token returns `401` with a `WWW-Authenticate` header that
  points at that metadata.
- An MCP client that signs in with the `nimbus.observe` scope can call `nimbus_get_capabilities`.
  For Claude Code, add the server with the client registration from step 1.7, then run `/mcp` in
  a `claude` session and authenticate `nimbus` in the browser:

  ```bash
  claude mcp add --transport http --client-id <mcp-client-app-id> --callback-port <port> nimbus https://<webapp>/mcp
  ```

In [private networking mode](spec/034-private-networking/spec.md), only clients inside the network
can reach `/mcp`. Cloud-hosted agents need a path through the Application Gateway.

## Limits

`/mcp` has its own rate-limit policy, `nimbus-mcp`: 60 requests per 60 seconds, per tenant,
client application and user. The action tools that change something (`nimbus_resubmit_message`,
`nimbus_skip_message`, `nimbus_set_message_reported` and `nimbus_classify_failure`) also share a
tighter limit: 5 per 60 seconds, for the same tenant, client and user (`[RateLimited]`).
`nimbus_get_capabilities` reports only the request limit. See [rate limiting](rate-limiting.md).

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
