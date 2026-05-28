# Feature Specification: Centralized Audit Log Service

Feature Branch: `008-centralized-audit-log-service`
Created: 2026-05-28
Updated: 2026-05-28
Status: Proposed (back-port candidate from DIS; see `BH.DIS.WebApp/Services/AuditLogging/AuditLogService.cs`).
Input: User description: "Every privileged action in the management WebApp — resubmit, skip, resubmit-with-changes, complete handoff, fail handoff, search events, view endpoint details, view event details, etc. — should leave an audit trail in two places: the message store (for durable, long-term, queryable records) AND Application Insights (for short-term ops dashboards). Today NimBus has the *reading* side wired (`pages/audits-list.tsx`, `components/audits/`, `IMessageTrackingStore` reads), but the *writing* side is scattered: `EndpointAuthorizationService.GetMessageAuditEntity(MessageAuditType)` builds an entity, and individual controllers / `AdminService` methods are expected to remember to call it and persist it. The result is uneven coverage — some actions log, some don't, and 'access denied' branches in particular get missed. We want a single service that every privileged-action handler invokes with a single line, with a small, explicit `AuditEntityType` enum naming the action."

## Problem

NimBus has audit *reading* infrastructure: the WebApp surfaces an Audits page (`pages/audits-list.tsx`), an audits panel on the endpoint and event details pages, and a backing `IMessageTrackingStore` that returns rows per-event, per-endpoint, and per-time-window. The shape of those rows is already defined: `MessageAuditEntity { AuditorName, AuditTimestamp, AuditType }`.

The *writing* side, however, is decentralised:

- `EndpointAuthorizationService.GetMessageAuditEntity(MessageAuditType type)` constructs a `MessageAuditEntity` with the current user's name and a timestamp.
- The controller / `AdminService` method that performed the action is expected to call `GetMessageAuditEntity(...)`, populate the rest of the entity, and persist it to the message store.
- There is no enforcement: a controller that forgets to call it leaves no audit trail. An access-denied branch that returns early without calling it leaves no record of the attempted access. A bulk action that loops over events may or may not produce one row per event vs. one row per batch — depending on the author.

The DIS fork addressed this with a small, explicit `AuditLogService` (`Services/AuditLogging/AuditLogService.cs`) that:

1. Exposes a single `LogAudit(type, context, accessDenied, data, eventId, endpointId)` method.
2. Writes the audit entry to *both* the message store (durable; surfaced by the existing audit-list endpoints) *and* Application Insights (short-term ops dashboards, easy KQL queries on `AuditEvent`).
3. Is invoked from every privileged-action path, including the access-denied branches (so the audit row records "user X tried to do Y but did not have permission").

The result in DIS is uniform coverage. The same line — `_auditLogService.LogAudit(type, _context, …)` — appears at the top of every handler, mirrored on the access-denied branch with `accessDenied: true`. It is one-glance verifiable in code review that an action either logs or doesn't.

NimBus today has the audit-reading infrastructure but no centralized write-side that closes this loop. This spec adds it.

## Scope

In scope:
- A new `IAuditLogService` interface + `AuditLogService` implementation in `NimBus.WebApp.Services/` that exposes a single `LogAuditAsync(...)` method (async; matches the underlying store's `Task` contract).
- Persistence to *both* the existing message store (`IMessageTrackingStore.StoreMessageAudit(...)`) *and* Application Insights (via `ILogger` structured logging with a defined scope key).
- An **entity migration**: extending `NimBus.MessageStore.MessageAuditEntity` with the four fields needed to describe a privileged-action audit row (`AccessDenied`, `Data`, `EventId`, `EndpointId`) — see FR-070..FR-073 — and the corresponding SQL Server schema bump (`Schema/0005_Audits_AccessDenied_Data.sql`). The Cosmos provider serializes the entity directly so it picks the new fields up at the model layer without a schema change; the in-memory provider follows automatically.
- Extending the existing `MessageAuditType` enum with the new privileged-action members listed in FR-010.
- Retrofitting the existing privileged-action paths in the NimBus WebApp — every controller method and every `AdminService` operation that touches state on behalf of a user — to call `LogAuditAsync(...)`. This includes the access-denied branches in each of those methods.
- Documentation in `docs/webapp-rest-api.md` and a new section in `docs/architecture.md` covering the audit-write contract.

Out of scope:
- Changes to the audit-*reading* APIs or UI. Those continue to read `MessageAuditEntity` via the existing endpoints; the four new entity fields surface as additional response properties (additive — older clients ignore them, newer surfaces can render them).
- A new audit storage backend. The existing store is the durable sink.
- Multi-tenant audit isolation, audit retention policies, or audit-store size budgets — those continue to be operational concerns owned outside the service.
- A general-purpose "audit any action anywhere" framework. The scope is the WebApp's privileged-action surfaces and the small set of audit-worthy actions enumerated below. The SDK / adapter side does not gain an audit hook.
- Migrating existing call sites away from `EndpointAuthorizationService.GetMessageAuditEntity` in a single commit. A bridge is retained for backwards compatibility while call sites are migrated.

## User Scenarios & Testing

### User Story 1 - Every privileged action produces exactly one audit row (Priority: P1)

As a compliance-conscious operator, I want each invocation of Resubmit / Skip / Resubmit-with-changes / Complete Handoff / Fail Handoff / Search Events / View Endpoint Details / View Event Details to produce exactly one audit entry in both the message store and Application Insights, naming the auditor, the action, the event and endpoint affected, and a timestamp.

Why this priority: This is the central guarantee the spec exists to provide. Without uniform coverage the audit trail is decorative rather than authoritative.

Independent Test: Invoke each privileged action through the WebApp API. After each call, query the message store for a single audit row with matching `AuditType`, `EventId`, `EndpointId`, and `AuditorName`. Query Application Insights traces for the same row.

Acceptance Scenarios:

1. Given a user calls `POST /api/events/{eventId}/resubmit`, When the call succeeds, Then exactly one audit row exists with `AuditType = Resubmit`, `AuditorName = <user-name>`, `EventId = <eventId>`, `EndpointId = <endpointId>`, `AccessDenied = false`, `AuditTimestamp` set to UTC now.
2. Given the same call succeeds, When App Insights is queried, Then exactly one trace event is present with the structured properties `AuditType=Resubmit`, `AuditorName=<user-name>`, `EventId=<eventId>`, `EndpointId=<endpointId>`, `AccessDenied=False`.
3. Given the user calls the same endpoint twice in succession, When the audit store is queried, Then two distinct rows exist (one per call).
4. Given an admin-side bulk action (`AdminService.ResubmitBatch(...)`) processes N events, When the action completes, Then N audit rows exist (one per event), each with the same `AuditorName` and `AuditTimestamp` granularity but distinct `EventId`.

---

### User Story 2 - Access-denied branches still produce an audit row (Priority: P1)

As a security-conscious operator, I want a row to be written when a user *attempts* a privileged action and is rejected by `EndpointAuthorizationService` — not just when the action succeeds — so that probing patterns are visible in the audit trail.

Why this priority: Today's pattern of "return early on access-denied" leaves no record of the attempt. This is the most common gap in the current scattered audit-write story.

Independent Test: Invoke a privileged action as a user who lacks permission on the target endpoint. The call returns 403. The audit row exists with `AccessDenied = true`.

Acceptance Scenarios:

1. Given a user without `EIP_Management` group and not in the endpoint's role assignments calls `POST /api/events/{eventId}/resubmit` for that endpoint, When the call is rejected by `IsManagerOfEndpoint(...)`, Then exactly one audit row exists with `AuditType = Resubmit`, `AccessDenied = true`, the user's `AuditorName`, the event and endpoint id.
2. Given the same scenario, When App Insights is queried, Then the trace event includes `AccessDenied=True`.
3. Given the access-denied row is written, When the audit-list page is opened, Then the row is visually distinguishable (badge / status column) from a successful row.

---

### User Story 3 - View / read actions are audited where relevant (Priority: P2)

As an operator answering "who accessed this event's data?", I want `GET /api/events/{id}` and `GET /api/endpoints/{id}` (and the search endpoints) to produce an audit row, so that PII-bearing data access is traceable.

Why this priority: Reads on PII-bearing surfaces are a real compliance concern. Less critical than write-action coverage but still in scope.

Independent Test: Open the Event Details page for a sensitive event as a user with permission. Confirm a `GetEventDetails` audit row exists. Repeat as a user without permission. Confirm a `GetEventDetails` row with `AccessDenied = true` exists.

Acceptance Scenarios:

1. Given a user opens an event details page (triggers `GET /api/events/{id}`), When the call succeeds, Then a `GetEventDetails` audit row exists.
2. Given a user opens the search page with a non-trivial filter, When the search call succeeds, Then one `SearchEvents` audit row exists with `Data = <filter JSON>` so the query parameters are themselves recorded.
3. Given the search is rejected for endpoint-scope reasons, When the call returns 403, Then one `SearchEvents` row with `AccessDenied = true` exists.

---

### User Story 4 - Bridge keeps legacy callers compiling during the migration (Priority: P2)

As a NimBus maintainer, I want `EndpointAuthorizationService.GetMessageAuditEntity(MessageAuditType)` to keep working as a thin pass-through during the migration window, so I do not have to convert every call site in one commit.

Why this priority: The migration set is large (≥ 20 call sites). A bridge lets the work happen incrementally without a single high-risk landing.

Independent Test: Build and test the solution after introducing the new service but before migrating any caller. All existing tests pass. Then migrate one controller; build and test again. Still green.

Acceptance Scenarios:

1. Given a controller still calls `_endpointAuth.GetMessageAuditEntity(MessageAuditType.Resubmit)` and persists the result directly, When the build runs, Then no warning or error fires (the method is still public).
2. Given a controller migrates to `_auditLog.LogAudit(...)`, When the build runs, Then both code paths coexist and produce equivalent audit rows.
3. Given the migration is complete and every caller has moved, Then `GetMessageAuditEntity` is marked `[Obsolete]` with a pointer at `LogAudit` and the bridge is removed in a follow-up.

---

### User Story 5 - Audit failures do not break the user action (Priority: P1)

As an operator, I want a transient audit-store failure (Cosmos throttle, SQL timeout, App Insights ingestion glitch) to NOT cause my Resubmit / Skip / etc. to fail with a 500. The action should succeed; the audit gap should be logged as a warning.

Why this priority: The audit trail must be best-effort durable. A hard dependency would couple every action's reliability to the audit-store SLA, which is wrong by design.

Independent Test: Inject a faulting audit-store stub. Invoke a privileged action. The action returns 2xx. The application log contains a warning naming the failed audit entry.

Acceptance Scenarios:

1. Given the message store's audit write fails with a transient error, When a privileged action is invoked, Then the action returns its normal success response and the application log includes a `LogWarning` with the entity fields and the underlying exception.
2. Given the App Insights sink is degraded, When a privileged action is invoked, Then the action returns its normal success response (the App Insights sink is asynchronous and best-effort; failures surface in the standard Microsoft.Extensions.Logging telemetry).
3. Given both sinks fail, When a privileged action is invoked, Then the action returns its normal success response and the application log includes a warning naming both failures.

---

## Edge Cases

- User principal is anonymous (e.g., a misconfigured route bypassed auth). The audit row should record `AuditorName = "anonymous"` or `null` rather than refuse to write. The action it tracks is still auditable.
- User principal has no `name` / `ClaimTypes.Name` / `preferred_username` claim. Service falls back to the user object id, then to `"unknown"`. Never throws.
- Bulk action (`AdminService.ResubmitBatch`) parallelises N events. The service MUST be safe to call concurrently from multiple threads in the same scope. (Audit entries are immutable; writes are independent.)
- The same action is invoked twice for the same event in the same second. Both rows exist; they are distinguished by their persisted row id, not by composite key. No deduplication.
- A user with the `BypassEndpointAuthorization` config flag set invokes an action. The audit row records the action with `AccessDenied = false`. A note in the entity's `Data` field (or a separate flag in a follow-up) indicates the bypass — but v1 does not require this distinction.
- The audit message-store backend rejects a payload whose `Data` field exceeds the provider's column limit (SQL Server `nvarchar` truncation, Cosmos document size). Service truncates `Data` to a safe size (4 KB suggested), appends `"… [truncated]"`, and persists the row. Documented in NFR-004.
- App Insights connection string is missing (dev mode). The service still writes to the message store; the App Insights write becomes a no-op (the underlying `ILogger` either no-ops or routes to console). No exception.
- Multiple privileged actions in a single HTTP request (rare — e.g., a composite admin endpoint that does both a Resubmit and a Skip in one call). Each writes its own row. The service is invoked twice from the same controller method.

## Requirements

### Functional Requirements

#### Service contract

- FR-001: A new interface `IAuditLogService` MUST be added to `NimBus.WebApp.Services/`, exposing:
  - `Task LogAuditAsync(MessageAuditType type, HttpContext context, bool accessDenied = false, string? data = null, string? eventId = null, string? endpointId = null, string? eventTypeId = null, CancellationToken cancellationToken = default);`
  - The method is async because the underlying `IMessageTrackingStore.StoreMessageAudit(...)` is `Task`-returning. A `void` contract would force fire-and-forget writes, defeating the "exactly one row" guarantee in SC-001 and the failure-handling story in User Story 5 (which depends on the caller awaiting the write to land or fail visibly).
- FR-002: A concrete `AuditLogService` MUST implement `IAuditLogService`, taking `ILogger<AuditLogService>`, `IHttpContextAccessor` (used by partials migrating from `EndpointAuthorizationService`), and the existing `IMessageTrackingStore` as constructor dependencies.
- FR-003: The service MUST be registered as a scoped service in `Startup.cs`:
  - `services.AddScoped<IAuditLogService, AuditLogService>();`
  - Scoped (not singleton) so the captured `IMessageTrackingStore` lifetime matches the request, consistent with how the existing message-store consumers are wired.
- FR-004: The `HttpContext` parameter is required (not nullable). Callers without a context (background services) MUST NOT consume this surface in v1 — this is a WebApp-scoped service.

#### Action enumeration

- FR-010: The existing `NimBus.MessageStore.MessageAuditType` enum MUST be extended with the new privileged-action members. Current members (`Resubmit`, `ResubmitWithChanges`, `Skip`, `Retry`, `Comment`, `CompleteHandoff`, `FailHandoff`) stay. Add:
  - `SearchEvents`
  - `GetEventDetails`
  - `GetEndpointDetails`
  - `EnableEndpoint`
  - `DisableEndpoint`
  - `PurgeMessages`
  - `Compose` (publish a new event from the WebApp)
- FR-011: The enum MUST continue to be persisted as a string (the SQL column is `NVARCHAR(50)` per `Schema/0004_Audits.sql`; the Cosmos provider serializes the value as-is). Adding members is non-breaking.
- FR-012: Adding a member MUST require no schema migration. New members render in the audit list as their stringified name; older rows continue to render as before.

#### Persistence

- FR-020: `LogAuditAsync(...)` MUST build a `MessageAuditEntity` populated from:
  - `AuditorName` — `context.User`'s `name` claim, falling back to `ClaimTypes.Name`, then `preferred_username`, then `"anonymous"` if no principal is available.
  - `AuditTimestamp` — `DateTime.UtcNow` (UTC consistently; matches NimBus's storage convention).
  - `AuditType` — the supplied `MessageAuditType`.
  - `Comment` — left as the caller-supplied comment string when migrating from the legacy `EndpointAuthorizationService.GetMessageAuditEntity(...)` path; otherwise null. The `Data` parameter is a structured "what was the action context" string and is NOT the same field as `Comment` (which is operator-typed free text).
  - `AccessDenied`, `Data` — the supplied parameters, populated onto the new entity fields per FR-070.
  - `EventId`, `EndpointId` — supplied to `IMessageTrackingStore.StoreMessageAudit(eventId, entity, endpointId, eventTypeId)` as separate arguments per the existing API; ALSO mirrored onto the entity fields per FR-070 so downstream readers do not need a side-channel join.
- FR-021: The service MUST persist the entity by awaiting `IMessageTrackingStore.StoreMessageAudit(eventId, entity, endpointId, eventTypeId)`. The existing method signature is reused; no new write path is added.
- FR-022: The service MUST write a structured log event to App Insights via `ILogger.BeginScope(dictionary)` followed by `LogInformation("Webapp AuditEvent occurred")` (or an equivalent message string). The dictionary MUST contain every field on the entity, with keys matching `nameof(MessageAuditEntity.XXX)`. Application Insights operators can then query traces with `where message == "Webapp AuditEvent occurred" | extend AuditType = customDimensions.AuditType` etc.
- FR-023: If the `StoreMessageAudit` write throws, the service MUST catch, log the exception via `LogWarning`, and continue. The App Insights write MUST still be attempted.
- FR-024: If the App Insights write throws (extremely rare with `Microsoft.Extensions.Logging`), the service MUST catch, log via `LogWarning` to a console sink (or skip — the failure path is best-effort), and not propagate.
- FR-025: The service MUST NOT throw under any circumstances reachable from the caller. Internal failures are absorbed; the caller's action proceeds.

#### Caller migration

- FR-030: Every controller method and `AdminService` operation listed in FR-010 MUST be updated to call `LogAudit(...)`:
  - On entry, before any authorization check, the call MUST be deferred so the entity reflects the *outcome* of the check.
  - If the authorization check rejects, the service MUST be called with `accessDenied: true` and the method MUST then return its 403 / 401 response.
  - On success, the service MUST be called with `accessDenied: false` after the action completes but before the response is shaped (so that a successful action that then fails on a downstream check still records the access).
- FR-031: For bulk actions (`AdminService.ResubmitBatch`, etc.), one `LogAudit(...)` call MUST be made per event, NOT per batch. The audit trail must be per-event to match the audit-reading UI's expectation.
- FR-032: For search actions, the `Data` parameter MUST receive the search filter serialized as JSON (`body.ToJson()` style). Operators querying "what searches has user X run?" need the filter, not just the action.
- FR-033: `EndpointAuthorizationService.GetMessageAuditEntity(MessageAuditType)` MUST remain available during the migration. After every caller has migrated, the method is marked `[Obsolete("Use IAuditLogService.LogAuditAsync", error: false)]` and removed in a follow-up release.

#### Entity & storage migration

- FR-070: `NimBus.MessageStore.MessageAuditEntity` MUST gain four nullable fields:
  - `bool AccessDenied { get; set; }` — defaults to `false`; serializes / projects as a bool column.
  - `string? Data { get; set; }` — structured action context (e.g., serialized search filter, body JSON for ResubmitWithChanges).
  - `string? EventId { get; set; }` — mirrors the value passed to `StoreMessageAudit(eventId, …)` so audit readers do not have to join on a side channel.
  - `string? EndpointId { get; set; }` — same rationale.
- FR-071: A new SQL Server migration script `Schema/0005_Audits_AccessDenied_Data.sql` MUST add `AccessDenied BIT NOT NULL CONSTRAINT [DF_MessageAudits_AccessDenied] DEFAULT (0)` and `Data NVARCHAR(MAX) NULL` columns to `[$schema$].[MessageAudits]`. Existing `EventId` and `EndpointId` columns (already present on the table per `Schema/0004_Audits.sql`) are reused — the migration MUST NOT add duplicate columns.
- FR-072: The SQL Server provider's audit-read mapper MUST project the two new columns onto the entity; the audit-write mapper MUST persist them.
- FR-073: The Cosmos DB provider stores `MessageAuditEntity` as a serialized document — the four new fields surface automatically once the entity gains them. The in-memory provider in `NimBus.Testing` requires no change beyond the entity-level addition.
- FR-074: Existing audit rows written before this migration MUST continue to read cleanly: `AccessDenied` defaults to `false`, `Data` defaults to `null`. The audit-list UI renders unchanged for those rows.

#### Documentation

- FR-040: `docs/webapp-rest-api.md` MUST gain a section ("Audit") listing the actions that produce audit rows, their `AuditEntityType` value, and the App Insights query template for finding them.
- FR-041: `docs/architecture.md` MUST gain a short subsection documenting the audit-write contract: who calls it, where it writes, what guarantees it provides (best-effort), and what it does not (no transactional coupling to the user action).
- FR-042: Inline XML doc comments on `IAuditLogService.LogAudit` and the `AuditEntityType` members MUST explain when each member fires.

#### Tests

- FR-050: Unit tests for `AuditLogService` MUST cover:
  1. `LogAuditAsync` populates `AuditorName` from each claim type in priority order, falling back to `"anonymous"`.
  2. `LogAuditAsync` awaits the message-store write AND emits the logger scope.
  3. Message-store write failure does not propagate; warning is logged.
  4. App Insights write failure does not propagate.
  5. `LogAuditAsync(accessDenied: true)` produces an entity with that flag set.
  6. The four new entity fields (`AccessDenied`, `Data`, `EventId`, `EndpointId`) are populated correctly on the entity passed to the store.
- FR-051: Integration tests on at least three migrated controller methods (suggested: `EventsController.Resubmit`, `EventsController.Skip`, `AdminService.PurgeMessages`) MUST verify a row appears in the message store after a successful call and after a rejected (access-denied) call.
- FR-052: The existing storage conformance suite (`NimBus.MessageStore.*Tests`) MUST gain test cases verifying that `AccessDenied` and `Data` round-trip across the SQL, Cosmos, and in-memory providers, and that legacy rows (pre-migration) project with `AccessDenied = false` and `Data = null`.

### Non-Functional Requirements

- NFR-001: The service's `LogAuditAsync` MUST complete in O(1) time — no enumeration of stores, no per-call DI scope creation. The single `await StoreMessageAudit(...)` is the only round-trip.
- NFR-002: The two writes (message store + App Insights) MUST NOT be wrapped in a transaction. They are independent best-effort sinks.
- NFR-003: The service MUST be safe to invoke concurrently from parallel handlers within the same request (bulk operations). Per-call entity construction + scoped lifetime satisfies this naturally.
- NFR-004: The `Data` field MUST be truncated to a safe storage limit (suggested 4 KB) before persistence. Truncation appends `"… [truncated]"` so audit reviewers see the marker.
- NFR-005: No PII leakage from the `Data` field. Callers MUST pass already-masked content; the service does NOT run additional masking. (PII masking is a separate concern, owned upstream by the event-DTO factory / log enricher per spec backlog items.)
- NFR-006: No new NuGet dependencies. `ILogger`, `IHttpContextAccessor`, and the existing message-store interfaces are sufficient.
- NFR-007: The service MUST be `internal` only if used solely within the WebApp; public if used by tests or extensions. Default: `public` to keep test reach simple.

## Key Entities

- **`IAuditLogService` / `AuditLogService`** — new service. Scoped lifetime. Owns the audit-write contract. Method: `Task LogAuditAsync(...)`.
- **`MessageAuditType` enum** — existing; extended with the new privileged-action members. Stored as a string. Forward-compatible.
- **`MessageAuditEntity`** — existing storage entity, **extended** with four new fields (`AccessDenied`, `Data`, `EventId`, `EndpointId`) per FR-070. The four fields are nullable / defaulted so legacy rows project unchanged.
- **`Schema/0005_Audits_AccessDenied_Data.sql`** — new SQL Server migration. Adds two columns; reuses the `EventId` / `EndpointId` columns already present from `Schema/0004_Audits.sql`.
- **App Insights "Webapp AuditEvent occurred" trace** — well-known message string, with structured properties matching the entity's fields. Operators query via this string.
- **`EndpointAuthorizationService.GetMessageAuditEntity`** — existing method. Bridged during migration, then obsoleted.

## Success Criteria

### Measurable Outcomes

- SC-001: Every privileged action enumerated in FR-010 produces exactly one row in the message store on each invocation. Verified by integration test against the in-memory message store.
- SC-002: Every access-denied path on those same actions produces exactly one row with `AccessDenied = true`. Verified by integration test.
- SC-003: App Insights traces for a representative test run contain one `Webapp AuditEvent occurred` event per privileged action, with structured properties present.
- SC-004: A faulting audit sink does not cause any privileged-action HTTP response to fail. Verified by injecting a throwing store and observing a normal 2xx response with a logged warning.
- SC-005: After full migration, the only consumer of `EndpointAuthorizationService.GetMessageAuditEntity` is the `IAuditLogService` implementation itself (or nothing). The method is marked `[Obsolete]`.
- SC-006: The unit-test suite (FR-050) and the integration-test scenarios (FR-051) pass.
- SC-007: The audit-list page renders the new rows identically to existing rows (no UI regression). New `AuditEntityType` values stringify cleanly in the list.

## Assumptions

- The existing `IMessageTrackingStore.StoreMessageAudit(eventId, entity, endpointId, eventTypeId)` is the canonical audit-write path; the audit-reading endpoints (`GetMessageAudits`, `SearchAudits`) consume from the same table / container. Verified.
- The SQL Server provider runs DbUp-managed migrations in numeric order; adding `Schema/0005_Audits_AccessDenied_Data.sql` is the same pattern used by every previous storage change.
- App Insights is the desired short-term ops sink. NimBus uses `Microsoft.Extensions.Logging` directly; the App Insights provider is already wired via `services.AddApplicationInsightsTelemetry()` (or equivalent) in `Startup.cs`. The service's structured-log call routes naturally through it.
- A small fixed enumeration of actions is sufficient. Adapter-side or extension-side actions do not need an audit hook in v1.
- The `Data` field's storage budget on the current message-store providers (Cosmos / SQL / in-memory) is at least 4 KB after truncation. (Cosmos has a ~2 MB document size; SQL Server `NVARCHAR(MAX)` is unbounded; in-memory is unbounded. 4 KB is comfortably inside all three.)

## Out of Scope

- An audit hook for SDK / adapter / background-service surfaces. WebApp-scoped only.
- A query API for "give me every audit event for user X across all endpoints in the last 24h." The existing `IMessageTrackingStore` per-endpoint / per-event readers continue to serve the audit-list UI.
- Transactional coupling of the audit write with the underlying action. Best-effort only.
- Cryptographic signing / chain-of-custody on audit rows. Not required by the platform's compliance posture.
- Per-action retention policies. All audit rows share the message store's retention.
- A separate "audit-only" sink (Splunk, Azure Log Analytics workspace). App Insights + message store is the union.

## Open Questions

- **PII handling in the `Data` field.** Search-filter payloads can include event-content snippets. Strategy: rely on upstream maskers (the event-DTO factory described in spec backlog item #14 / log enricher in backlog #9) to ensure callers pass already-masked content. *No action in this spec; called out as a dependency.*

## Resolved Questions

- One row per event for bulk actions (not one row per batch). Resolved — matches the audit-reading UI's expectation and gives compliance reviewers the right granularity.
- Access-denied branches MUST write. Resolved — the most common gap in today's scattered story.
- Read actions (`GetEventDetails`, `GetEndpointDetails`, `SearchEvents`) MUST write. Resolved — PII-bearing reads are compliance-relevant.
- The service is best-effort. Failures absorb and log; the user action proceeds. Resolved — the alternative (audit write blocks the action) is wrong by design.
- The legacy `GetMessageAuditEntity` method is bridged during migration, then obsoleted. Resolved — single-commit migration is too risky on a service this widely used.
- The service is WebApp-scoped (singleton). Resolved — adapter / SDK surfaces are out of scope.
- App Insights and message store are independent sinks; no transactional coupling. Resolved — matches DIS and matches the "best-effort" non-functional design.
