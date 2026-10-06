# Spec 035 Phase 2a (Operate): implementation plan

Status: implemented (PRs 1-3 merged 2026-10-06 as #209, #210 and #211; PR 4 is the documentation).
The delegated Azure pilot is open. See [As built](#as-built).

## Context

Phase 1 shipped read-only operator MCP tools and passed its Azure pilot on 2026-10-06 (PR #207;
null-member fix PR #208). Phase 2a lets an operator using an agent (delegated, interactive only)
**resubmit, skip, mark reported and classify** failed messages over `/mcp`.

Spec decision 6 requires:
- a current-state guard: still failed, same latest attempt;
- an audit before the side effect;
- fresh ACL checks;
- no new store.

The guard must live in a coordinator shared with REST, so a Web UI click and an agent cannot both
act on the same message. Exit criteria: stale-state and UI-vs-agent race tests, then a delegated
pilot.

### What exploration found (drives the design)

- **REST resubmit and skip have no state guard.**
  - Code: `src/NimBus.WebApp/Controllers/ApiContract/EventImplementation.OperatorActions.cs` —
    resubmit L34-105, skip L107-151, report L153-197, resubmit-with-changes L282-363.
  - They accept any stored message id (`GetMessageWithFallback`, `EventImplementation.Queries.cs:431`).
  - They publish (`IManagerClient.Resubmit`/`Skip`, `src/NimBus.Manager/ManagerClient.cs`), then call
    `ArchiveFailedEvent`, which is unconditional in Cosmos and SQL.
  - They write the audit after the side effect. `LogAuditAsync` swallows store failures, and
    admins can switch these audit types off.
- **There is no atomic compare-and-swap on a failed row.** The precedent to copy is
  `TrySkipDeferredMessage(eventId, sessionId, endpointId, expectedLastMessageId, expectedUpdatedAt)`:
  a default interface method that throws `NotSupportedException`, implemented in Cosmos (ETag), SQL,
  in-memory, the conformance suite and `InstrumentingMessageTrackingStoreDecorator`, and shipped in
  minor v3.9.0.
- **Row version precedent:** `DeferredMessageInspector.RowVersion` =
  `"{SessionId}:{LastMessageId}:{UpdatedAt.Ticks}"`.
- **The UI always sends the row's `lastMessageId`.** It offers resubmit/skip on Failed, DeadLettered,
  Unsupported and Pending+Handoff (manual override FR-042).
- **The ACL snapshot is cached** for 45 s (`AccessControlSnapshotProvider`); there is no fresh path.
- **MCP tools are registered explicitly** (`McpOperatorServiceCollectionExtensions.cs` L68-70,
  `.WithTools<T>(ToolSerializerOptions)`).
  - Errors are `McpException("[Code] …")` (`Mcp/Operations/OperatorToolErrors.cs`).
  - `PermittedActions` is hard-coded `[]` (`OperatorDiscoveryTools.cs` L63).
  - Tests pin the read-only tool list (`McpOperatorEndpointTests.cs` L18-23, L68, L101).
  - `StubAuthorization` grants Reader only (`McpTestHost.cs` L251-269).
- **Classification** is `FailureClassificationService.AnalyzeAsync(eventId, messageId, idempotencyKey
  (GUID), force)`. It already checks Contributor, eligibility (Failed/DeadLettered), idempotency and
  audit.
- **Data Protection** is already registered (`Startup.cs:41`).

## Design

### Guard: claim first, then publish
1. Resolve the row (`GetEvent` / `GetFailedEvent`). Build `MessageVersion` = opaque base64url of
   `{status, sessionId, lastMessageId, updatedAt.Ticks}`.
2. Fresh ACL check: Contributor on the endpoint.
3. Strict audit: the action is recorded, or the command is refused.
4. **Atomic claim:** `TryArchiveUnresolvedEvent(eventId, sessionId, endpointId, expectedLastMessageId,
   expectedUpdatedAt)`. Only one caller wins. The loser gets `StaleMessage` (REST 409) and nothing
   is sent.
5. Publish via `IManagerClient`.
6. If the publish throws, `TryRestoreArchivedEvent(…same version…)`, write a `CommandNotSent` audit,
   and return an error.

Rationale: publish-then-archive (today's order) cannot stop two racing callers from both
publishing. Claim-first prevents that. The Resolver's Pending write on receipt revives the row
exactly as it does today.

Remaining gap, documented and closed by 2b's journal: a crash between the claim and the publish
leaves the row archived with no command sent. The pre-side-effect audit row makes it findable.

### Store (no new store, two new methods)
Add to `IMessageTrackingStore`, following the `TrySkipDeferredMessage` precedent (default
interface method that throws `NotSupportedException`):
- `TryArchiveUnresolvedEvent(...)` — archives when `LastMessageId` and `UpdatedAt` match and the row
  is not deleted.
- `TryRestoreArchivedEvent(...)` — un-archives on the same version. Archive does not touch
  `UpdatedAt`.

Implement them in:
- Cosmos: read, then replace with `IfMatchEtag`, as in `TrySkipDeferredMessage`
  (`CosmosDbMessageTrackingStore.Writes.cs:42`);
- SQL: `UPDATE … WHERE … AND LastMessageId=@L AND UpdatedAt=@U AND Deleted=0`, checking the
  row count;
- in-memory (`src/NimBus.Testing/Conformance/InMemoryMessageStore.cs`);
- the forwarding facades `CosmosDbClient.cs` and `SqlServerMessageStore.cs`;
- `InstrumentingMessageTrackingStoreDecorator` (must forward);
- conformance tests in `MessageTrackingStoreConformanceTests.cs`.

### Shared coordinator: `src/NimBus.WebApp/Services/Operations/`
- `IOperatorCommands` / `OperatorCommandCoordinator`:
  - `ResubmitAsync`, `ResubmitWithChangesAsync` (REST only; payload edits are not an MCP tool
    until Phase 3), `SkipAsync`, `SetReportedAsync`, `PreviewAsync`.
  - Each takes an `OperatorCommandContext {Channel: WebApp|Mcp, Reason?, IdempotencyKey?,
    ClientId?}` and an expected `MessageVersion`.
  - It returns a result enum: `Accepted | Stale | Forbidden | NotFound | NotAllowed | AuditUnavailable
    | NotSent`.
- Moves `GetMessageWithFallback`, `LatestRequestMessageWithPayload`, the endpoint choice
  (`BlockedEventRules.IsSelfOriginating`) and `ResolveServerEventTypeIdAsync` out of
  `EventImplementation` into the coordinator. The PII-placeholder checks for resubmit-with-changes
  stay as they are.
- **Eligibility:**
  - REST keeps today's set: Failed, DeadLettered, Unsupported, Pending+Handoff.
  - MCP 2a allows Failed, DeadLettered and Unsupported. Handoff is Phase 3.
  - REST derives the expected version from the row when the request's `messageId ==
    row.LastMessageId`. Any other `messageId` is stale (409).
- **Fresh ACL:**
  - new `IAccessControlSnapshotProvider.GetFreshSnapshotAsync()`, which reads the store and
    refreshes the cache;
  - new `IEndpointAuthorizationService.HasRoleFreshAsync(role, endpointId)`, which bypasses the
    per-request memo and fails closed on a store error.
  - Both are added to existing WebApp interfaces with a default-method bridge, as in the store
    precedent.
- **Strict audit:**
  - new `IAuditLogService.LogRequiredAuditAsync(...)` throws if the row is not persisted;
  - `Resubmit`, `ResubmitWithChanges`, `Skip` and `ReportEvent` move to `AlwaysRecorded` in
    `AuditSettingsProvider`, so admins can no longer switch off audits for mutations;
  - audit `data` JSON: `{channel, reason, idempotencyKey, clientId, priorStatus, messageVersion}`;
  - a new `MessageAuditType.CommandNotSent` is appended at the end of the enum, because Cosmos
    stores the numeric value.
- **REST:** the operator actions in `EventImplementation.OperatorActions.cs` delegate to the
  coordinator.
  - `api-spec.yaml` adds `409` to resubmit, skip and change, so NSwag regenerates the contracts.
  - The ClientApp shows "This message changed since you loaded it — refresh" on 409 (event details
    and failed-messages batch).
  - Report keeps last-writer-wins: there is no state guard, but the audit now happens before the
    store write.

### MCP tools: `src/NimBus.WebApp/Mcp/Tools/OperatorActionTools.cs`
- **Scopes:** add delegated `nimbus.resubmit`, `nimbus.skip`, `nimbus.annotate` and `nimbus.classify`
  to `McpOperatorPermissions` and to `ScopesSupported` in the metadata.
  - Write tools require a delegated `scp`; a token with only app roles gets `PermissionDenied`
    (2a is interactive only).
  - Local-dev skips scope checks, but role checks still run, as in `OperatorPayloadAccess`.
- **Action token:** `nimbus_prepare_action({action: resubmit|skip, endpointId, eventId,
  messageVersion})`.
  - Read-only. It checks the action's scope, role and eligibility.
  - It returns `{actionToken, expiresInSeconds: 120, affectedMessages: 1, laterBlockedMessages,
    expectedStatus}`. For skip, `laterBlockedMessages` is the deferred count from the query behind
    `nimbus_get_session`.
  - The token is an `ITimeLimitedDataProtector` payload (purpose `NimBus.Mcp.ActionToken.v1`)
    binding action, endpoint, event, session, version, `oid`, `tid`, `azp` and environment.
- **Execute tools:**
  - `nimbus_resubmit_message` / `nimbus_skip_message({actionToken, idempotencyKey (GUID), reason
    1-1000})`: unprotect the token and match the caller, then call the coordinator. The result is
    `{status: Accepted, commandSent: true}`; the agent re-reads the message to see the outcome.
  - Annotations: resubmit `ReadOnly=false, Idempotent=false`; skip adds `Destructive=true`.
- **`nimbus_set_message_reported({endpointId, eventId, reported, ticketId?, reason,
  idempotencyKey})`:** uses the annotate scope and goes through the coordinator.
- **`nimbus_classify_failure({eventId, messageId, idempotencyKey, force?})`:** uses the classify
  scope and calls `FailureClassificationService.AnalyzeAsync`. Returns `FeatureUnavailable` when
  classification is off.
- **Reads:**
  - `nimbus_get_message` adds `messageVersion` and `eligibleActions`;
  - `nimbus_get_capabilities.permittedActions` lists the actions the caller may take on at least one
    endpoint.
- **Errors:** add `StaleMessage`, `ActionNotAllowed`, `RateLimited`, `OutcomeUnknown` and
  `AuditUnavailable` to `OperatorToolErrors`. An invalid or expired token maps to `StaleMessage`.
- **Mutation limit:**
  - `RateLimitOptions.McpMutations` (5 per 60 s);
  - a singleton `PartitionedRateLimiter` keyed like `McpPartitionKey`, applied inside the write
    tools, because every tool call shares the one `/mcp` HTTP endpoint.
- Registered via `.WithTools<OperatorActionTools>(ToolSerializerOptions)`.

## Delivery: four PRs, each Release-green; plan doc committed first
0. Commit this plan as `docs/plan/2026-10-06-mcp-operator-phase2a-plan.md`. Branch from
   `origin/master` after #207 and #208 merge.
1. **PR 1 `feat(storage)`:** the two conditional archive/restore methods, in every provider,
   conformance and the decorator.
2. **PR 2 `feat(webapp)`:**
   - coordinator, fresh ACL and strict audit;
   - REST routed through the coordinator, with the 409 contract and UI handling;
   - race test: two concurrent coordinator calls on one version → exactly one `IManagerClient` send.
3. **PR 3 `feat(webapp)`:** MCP scopes, token and the five tools; capabilities and get_message
   fields; mutation limiter; tests.
4. **PR 4 `docs`:**
   - `docs/mcp-server.md`: tool table, the four scopes in Entra setup, limits, "read-only" wording;
   - Spec 035 status and release-note entry;
   - delegated Azure pilot on `webapp-nbdemo-dev-management`: the user adds the four scopes to `NimBus MCP (dev)`
     and pre-authorizes the client.

## Verification
- `dotnet build src/NimBus.sln -c Release` (0 errors) and `dotnet test src/NimBus.sln -c Release
  --no-build`.
- Run the Cosmos and SQL conformance suites against local containers (vnext emulator and SQL
  container, per the conformance recipe); report anything skipped.
- `npm --prefix src/NimBus.WebApp/ClientApp run test:ci` and a `build` that leaves NSwag generation
  enabled (no `SkipSpaBuild`).
- Tests:
  - stale version → no send;
  - Web UI and MCP racing on one version → exactly one send;
  - a Reader or missing scope is denied;
  - an app-role-only token is denied on writes;
  - a token from another `oid`/`azp` or an expired token → `StaleMessage`;
  - audit store failure → refused, nothing sent;
  - publish failure → row restored and `CommandNotSent` audited;
  - mutation rate limit;
  - updated tool-list and annotation tests.
- End to end: `dotnet run --project src/NimBus.AppHost`.
  1. Produce a failed message with a sample.
  2. Via `/mcp` (local-dev): `nimbus_get_message` → `nimbus_prepare_action` →
     `nimbus_resubmit_message`, and see the row go Pending → Completed.
  3. Retry the same token: `StaleMessage`.
  4. In the Web UI, a stale resubmit shows the 409 toast; capture a screenshot for the PR.
- Delegated pilot in Azure with Claude Code (exit criterion).

## As built

Delivered as planned, with these differences:

- **Order inside a command:** claim, then audit, then publish. The plan listed the audit before
  the claim. Writing it after the claim means the loser of a race leaves no `Resubmit` row (the
  Web UI's resubmit count reads those rows), and the audit still precedes the only side effect
  that leaves the WebApp. If the audit write fails, the claim is released.
- **Store methods** also take the expected `ResolutionStatus`:
  `TryArchiveUnresolvedEvent(eventId, sessionId, endpointId, expectedStatus, expectedLastMessageId,
  expectedUpdatedAt)` and `TryRestoreArchivedEvent(...)`. Cosmos archives and restores by
  replace under the ETag; restoring puts back the unresolved-row TTL.
- **Fresh ACL check:** `IEndpointAuthorizationService.HasRoleFreshAsync` invalidates the snapshot
  cache and resolves again, bypassing the per-request memo. The existing invalidation semantics
  already fail closed on a store fault (only claim-based and code-defined grants remain), so no
  `GetFreshSnapshotAsync` was needed.
- **Web UI eligibility** is the set the UI actually offers: Failed, DeadLettered, Unsupported,
  Deferred, and Pending with the Handoff sub-status. MCP accepts Failed, DeadLettered and
  Unsupported.
- **Lookups:** the coordinator has `FindByMessageAsync` (REST: the row's latest message must be
  the one the page loaded) and `FindCurrentAsync` (MCP: by endpoint and event id, version checked
  by the caller).
- **Idempotency keys** must be GUIDs and are recorded in the audit row. They are not deduplicated
  yet; Phase 2b's journal does that. A retried token fails with `StaleMessage` once the first
  command is sent, because the row has moved on.
- **Errors:** `AuditUnavailable` was added to the planned codes. An invalid, expired or foreign
  token and a changed message all map to `StaleMessage`.
- **Report** goes through the coordinator with a required audit row, but keeps last-writer-wins:
  a marker is an annotation, not a state change.
- PR 3 carried #208 (null members in tool results), which the new results need; #208 merged first.

Verified: the Release build and the full test suite with live SQL Server and Cosmos DB containers
on each PR, and an end-to-end run on the local Aspire stack with the Service Bus emulator:
get, prepare and resubmit over `/mcp`; the replayed token refused; and a Web UI resubmit of the
same message refused with 409 after an agent acted first.

Remaining for the exit criterion: release, deploy to the nonproduction WebApp, add the four scopes
to the MCP app registration, and pilot the write tools with a delegated client.
