# Spec 035 Phase 1 (Observe): implementation plan

Status: in progress. Spec: [docs/spec/035-mcp-operator-access](../spec/035-mcp-operator-access/spec.md).

Phase 1 adds an opt-in, read-only operator MCP endpoint to the WebApp, deploys it to Azure and
retires `NimBus.Mcp`. It ships as four pull requests. Each one builds in Release and leaves `/mcp`
disabled unless configured.

## Verified starting points

- `ModelContextProtocol.AspNetCore` 2.2.0 targets net10.0 and supports protocol revisions
  2024-11-05 through 2026-07-28. Stateless Streamable HTTP is its default, and it ships an `AddMcp`
  authentication scheme that serves protected-resource metadata and the 401 challenge. It does
  not validate `Origin`, so NimBus must.
- Authentication ladder: `Startup.Security.cs` `AddAuthenticationStack`. The local-dev branch
  (Development + `EnableLocalDevAuthentication`) runs first; startup throws if the flag is set
  outside Development (line 25). The Entra branch registers `AddMicrosoftIdentityWebApi` on the
  default `"Bearer"` scheme behind the `"Az"` policy scheme.
- `EndpointAuthorizationService` reads `HttpContext.User` (`oid`, `groups`, email claims), so a tool
  running inside the MCP HTTP request sees the MCP-authenticated principal.
- Endpoint visibility: `EndpointImplementation.GetVisibleEndpointIdsAsync` (Reader filter);
  catalog from `IPlatform.Endpoints` (`IEndpoint.EventTypesProduced/Consumed`); environment name
  from configuration key `Environment`.
- Rate limiting: `RateLimiting/RateLimitingServiceCollectionExtensions.cs`, per-user fixed windows
  keyed by `UserPartitionKey`, applied after `UseAuthorization`.
- Test hosting pattern: `tests/NimBus.WebApp.Tests/AnonymousEndpointsTests.cs` (HostBuilder +
  TestServer composing only the pieces under test).

## PR 1: MCP foundation (this branch)

Configuration section `NimBus:Mcp`:

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Maps `/mcp`. Off means no route (404). |
| `EnableForLocalDevelopment` | `false` | Maps `/mcp` only when the local-dev bypass is on; otherwise a no-op. The Aspire AppHost sets it. |
| `AzureAd:Instance`, `TenantId`, `ClientId`, `Audience` | none | The MCP resource's own app registration (Entra mode). |
| `AllowedOrigins` | empty | Browser origins allowed to call `/mcp`; other `Origin` values get 403. |

Mode resolution (`McpAuthenticationMode`), evaluated once at startup:

1. Neither `Enabled` nor `EnableForLocalDevelopment` → **Disabled**: nothing registered or mapped.
   `EnableForLocalDevelopment` alone enables only step 2 and is otherwise **Disabled**.
2. Development and `EnableLocalDevAuthentication=true` → **LocalDevelopment**: `/mcp` authorizes
   with the existing `LocalDev` scheme; non-loopback callers get 403.
3. `AzureAd:ClientId` and `TenantId` set → **Entra**: named JWT scheme `NimBusMcp` via
   `AddMicrosoftIdentityWebApi(section, jwtBearerScheme: "NimBusMcp")`, plus the SDK `AddMcp`
   scheme for protected-resource metadata. Policy requires scope `nimbus.observe` (delegated `scp`)
   or app role `Nimbus.Observe` (workload `roles`).
4. Otherwise → startup fails with a message naming the missing keys.

Files:

- `src/NimBus.WebApp/NimBus.WebApp.csproj`: add `ModelContextProtocol.AspNetCore` 2.2.0.
- `src/NimBus.WebApp/Mcp/McpOperatorOptions.cs`, `McpAuthenticationMode.cs`.
- `src/NimBus.WebApp/Mcp/McpOperatorServiceCollectionExtensions.cs`: `AddNimBusOperatorMcp` and
  `MapNimBusOperatorMcp`; explicit `WithTools<...>()` registrations, no assembly scan.
- `src/NimBus.WebApp/Mcp/McpRequestGuardMiddleware.cs`: `Origin` and loopback checks on the MCP path.
- `src/NimBus.WebApp/Mcp/Tools/OperatorDiscoveryTools.cs`: `nimbus_get_capabilities` (environment,
  caller, authentication mode, readable endpoints, available tools, limits) and
  `nimbus_list_endpoints` (Reader-filtered catalog with produced/consumed event types).
- `RateLimiting/`: `Mcp` policy and options, partitioned by tenant, client application and user.
- `Startup.cs`, `Startup.Pipeline.cs`: register and map.

Tests (`tests/NimBus.WebApp.Tests/Mcp/`), written first:

- Mode resolution: each row above, including the startup failure message.
- Disabled: `/mcp` returns 404.
- Local development: an MCP client lists exactly the registered tools; a foreign `Origin` gets
  403; a non-loopback caller gets 403.
- Entra: no token → 401 with a `resource_metadata` challenge; the metadata document is served;
  wrong audience or missing `nimbus.observe` → rejected; valid token → tools listed.
- `nimbus_list_endpoints` returns only endpoints the caller can read.

Verified (2026-09-25): the tests above pass, and they fail when the scope check or the `Origin`
check is disabled. Under Aspire, a real MCP client negotiated protocol 2026-07-28 against
`/mcp` with no sign-in and read the live catalog; a foreign `Origin` got 403 and a localhost
`Origin` 200. The Entra mode is covered by TestServer tests with locally signed tokens; it has
not run against a real Entra tenant yet (PR 4).

## PR 2: endpoint and message read tools

`nimbus_get_overview`, `nimbus_get_endpoint`, `nimbus_find_messages`, `nimbus_get_message`,
`nimbus_get_message_history`, `nimbus_get_session`. Include active Monitor acknowledgements in
overview and endpoint results.

Approach (changed from the first draft): rather than extracting logic out of the controllers,
`Mcp/Operations/OperatorQueries` calls the existing REST implementations in-process
(`IEndpointApiController`, `IEventApiController`, `IMonitorApiController`). Reader checks, PII
redaction and audit rows therefore match the Web UI exactly, and REST is untouched. Tools depend
on `OperatorQueries`, not the controllers, so logic can move behind it later without changing
tool contracts. `OperatorEndpointCatalog` refuses unreadable endpoints before any store call and
normalizes ids; 403 and 404 map to the same not-found error. Results never include payloads or
stack traces; error and log text is truncated to 2000 characters; timestamps are marked UTC.
`nimbus_find_messages` cursors wrap the store token and are bound to the query and caller.

Deferred: a reported-marker filter (the REST `EventFilter` has none), audit records in history,
and blocked-message detail in sessions.

## PR 3: search, metrics and classification reads

`nimbus_search_messages`, `nimbus_get_metrics` (site-Reader floor), `nimbus_get_classification`
(via `IIntegrationIntelligenceHost`), plus the payload-reveal path: `PiiReader` plus the
`nimbus.payload.read` scope, off by default.

As built: search and metrics go through the REST implementations like PR 2; a site-level 403
is reported as `[PermissionDenied]`. `nimbus_get_metrics` takes `view` (throughput, latency,
failures) and `period` (1h to 30d). `nimbus_get_classification` reads through
`IOperatorClassificationSource` over `FailureClassificationService`, which applies its own
Reader check; it reports `[FeatureUnavailable]` when classification is off, never starts an
analysis, and defaults to the latest attempt. `nimbus_get_message` returns the payload only with
`includePayload=true`, PiiReader and, for Entra callers, the delegated `nimbus.payload.read`
scope (no app role, so workloads never qualify); the reveal goes through the audited event-details
read and is logged. Payloads are capped at 64 KB. The protected-resource metadata advertises the
payload scope.

## PR 4: Azure deployment and NimBus.Mcp retirement

Bicep app settings for `NimBus__Mcp__*` preserved by the deployment, an Entra app registration
guide (App ID URI, `nimbus.observe` scope, `Nimbus.Observe` app role, groups claim), client setup
docs replacing `docs/mcp-server.md`, and removal of `src/NimBus.Mcp`, `tests/NimBus.Mcp.Tests`
and their solution entries. Release-note entry. Nonproduction Azure pilot before merge.

## Verification per PR

`dotnet build src/NimBus.sln -c Release` and `dotnet test src/NimBus.sln -c Release --no-build`;
report skipped live conformance suites.
