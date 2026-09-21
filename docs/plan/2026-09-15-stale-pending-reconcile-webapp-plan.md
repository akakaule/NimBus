# Implementation plan: Spec 032 stale-Pending reconcile in the WebApp

Date: 2026-09-15
Spec: `docs/spec/032-stale-pending-reconcile/spec.md` — does not exist yet; step 0 writes it from the
Design summary below. Parent: Spec 030 §9 (the repair procedure) and §12 item 2 (packaging, still open).
Reference implementation: `C:\Git\EET\EET.Deploy\ops\stale-pending-repair\` (EET.Deploy branch
`ops/stale-pending-repair`, commit f9aaee9) — a Cosmos-only console tool that implements §9 exactly,
with 24 xunit tests and an emulator run. Its `RepairRules.cs` is the rule to port; its
`CosmosRepairStore.ApplyAsync` is the Cosmos write to port.
Branch: `feat/stale-pending-reconcile` off `feat/stale-write-guard` (needs `StaleWriteGuard`, the
`Task<bool>` contract and the 030 conformance cases; rebase onto master once 030 merges). The
`C:\Git\NimBus` working tree has another session's uncommitted work; create the branch in a worktree:
`cd C:\Git\NimBus && git worktree add .worktrees/stale-pending-reconcile -b feat/stale-pending-reconcile feat/stale-write-guard`.
Version framing: 3.7.0 together with 030/031 (four docs on the guard branch already say "Since 3.7.0";
a new `IMessageTrackingStore` member is a minor bump either way).

Baseline facts (verified 2026-09-15 on `feat/stale-write-guard`):
- No component writes Completed except `ResolverService.UpdateState` → `UploadCompletedMessage`
  (`ResolverService.cs:730-752`). `HandoffCompletedRequest` is Manager → subscriber; the subscriber
  answers with a `ResolutionResponse` and requires the session to be blocked by that event
  (`StrictMessageHandler.cs:405-438`). `HandoffSettlementService.SettleAsync` 400s on a plain Pending
  row (`PendingSubStatus` null). There is no broker path that completes a stale ordinary Pending row.
- `UploadCompletedMessage` is unconditional on every provider (Cosmos plain `UpsertItemAsync` with
  `deleted = true`, ttl 30 d; SQL MERGE terminal branch, `Deleted = 0`, no TTL; in-memory
  `AddOrUpdate`, guard returns true for terminal). `IMessageTrackingStore` has no "replace only if
  still Pending with `LastMessageId` X" primitive. The Cosmos ETag pattern exists privately in
  `CosmosDbMessageTrackingStore.UploadGuarded` (`:1629-1708`, `IfMatchEtag` upsert, 412 handling).
- `IMessageTrackingStore` implementers (complete set, no mock frameworks): `CosmosDbMessageTrackingStore`
  + facade `CosmosDbClient`, `SqlServerMessageTrackingStore` + facade `SqlServerMessageStore`,
  `InMemoryMessageStore` (`src/NimBus.Testing/Conformance`), `InstrumentingMessageTrackingStoreDecorator`
  (`NimBus.OpenTelemetry`), test fakes `ThrowingStore` (`tests/NimBus.OpenTelemetry.Tests/StoreDecoratorTests.cs:168`),
  `FakeCosmosDbClient` (`tests/NimBus.Resolver.Tests/ResolverServiceTests.cs:675`), and the
  `RecordingStore` family in `tests/NimBus.WebApp.Tests/HeartbeatServiceTests.cs:578` (inherits
  `InMemoryMessageStore`, compiles automatically). Default interface members are the precedent for
  non-breaking additions (`GetResubmitCounts`, `SetEventReport`, `IMessageTrackingStore.cs:176-202`);
  the decorator forwards even default members explicitly.
- `ResolverService.CreateUnresolvedEvent` (`:644-698`) and `ComputeHandoffWallClockMsIfTerminal`
  (`:701-725`) are private instance methods; the WebApp cannot call them. `GetResultingStatus`
  (`:883`) projects any message with `DeadLetterErrorDescription != null` as DeadLettered first.
- Spec 030 §5.7 is implemented: `MessageContext.ScheduleRedelivery` keeps the original `MessageId`
  (`MessageContext.cs:737`). New incidents therefore no longer produce a second history document with
  a dashed-GUID id; the classifier must not rely on distinct copy ids.
- WebApp admin pattern: every `/api/admin/*` handler starts with `IsSiteOwnerAsync()` →
  `ForbidResult`, then `EndpointVerificationService.EndpointExists` → 404, then `IAdminService`
  (`AdminImplementation.cs:571-598`, `:782`). Mutations audit through
  `IAuditLogService.LogAuditAsync(type, _context, accessDenied:, endpointId:, data:)` on both the
  denied and the success branch; previews do not audit. `LogAuditAsync` cannot set
  `MessageAuditEntity.Comment` (only `Data`); a per-event audit with a comment goes through
  `IMessageTrackingStore.StoreMessageAudit` directly. `AuditLogService.ResolveAuditorName(HttpContext)`
  is `internal static` and usable from the controller.
- Paging pattern: `AdminService.Purge.cs:458-576` walks `GetEventsByFilter(new EventFilter { EndPointId,
  ResolutionStatus = statuses }, token, PageSize = 20)` from `string.Empty` until the token is null,
  collects per-row errors into `BulkOperationResult.Errors`. `EventFilter.MessageType` is a single
  value, so candidate filtering by the five request types is client-side on the Pending pages.
- Contract flow: edit `src/NimBus.WebApp/api-spec.yaml` → `dotnet build src/NimBus.WebApp` (the
  `NSwag` target, skipped when `SkipSpaBuild=true`, which the test csproj sets) regenerates the
  git-tracked `Controllers/ApiContract.g.cs` and `ClientApp/src/api-client/index.ts`. Admin routes are
  `/api/admin/endpoint/{endpointId}/<op>-preview` + `/<op>`, operationIds `post-admin-<op>-preview` /
  `post-admin-<op>`, tag `Admin`. Any Admin-tagged route under `/api/admin/` inherits the `nimbus-admin`
  rate policy (60/min per user); `RateLimitEndpointMetadataTests` asserts that set-equality.
- `MessageAuditType` is persisted numerically in Cosmos: new members are appended after
  `DeleteStorageContainer` (`MessageAuditEntity.cs:131`) and added to all THREE `auditType` enum lists
  in `api-spec.yaml` (`MessageAudit` ~3021, `AuditSearchFilter` ~4065, `AuditEntry` ~4536);
  `AuditTypeContractTests` enforces it. The Audit page derives labels (no label map to edit).
- The WebApp never pushes SignalR after its own store writes (purge, skip, delete); only
  `StorageHookImplementation` broadcasts. `IStoreResultCache` has no invalidation API.
- `StorageDependencyArchitectureTests`: services under `NimBus.WebApp.Services*` depend on
  `IMessageTrackingStore`, never `INimBusMessageStore`; no "cosmos" in narrow-contract field names.
- UI template: `SubscriptionPurgeCard` in `ClientApp/src/components/admin/advanced-operations.tsx:55-157`
  (preview → tinted summary → `ConfirmDestructiveAction` with `confirmText = endpoint id` →
  `OperationProgress`). Cards live in `components/admin/operations.tsx` groups with a hard-coded
  `count`. Test pattern: `vi.mock("api-client", …{ ...actual, Client: FakeClient, CookieAuth: () => ({}) })`,
  DTOs via `Object.assign(new api.X(), {...})`, dynamic import after mocks, `afterEach(cleanup)`.

Conventions that bite: Release builds turn CS warnings into errors and CS8767 is not allowlisted — a new
interface member implemented in three providers is exactly where it bites; build Release. MSTest
everywhere except `tests/NimBus.CommandLine.Tests`; test files start with
`#pragma warning disable CA1707, CA2007` (conformance suite also CA1515). Conformance: loop cases inside
ONE endpoint id with distinct event ids (each endpoint id is a Cosmos container on the emulator), fresh
`SampleEvent`/`GuardedEvent` per call; Cosmos run needs `NIMBUS_COSMOS_TEST_CONNECTION` (+
`NIMBUS_COSMOS_TEST_GATEWAY=1`), SQL run `NIMBUS_SQL_TEST_CONNECTION`; CI fails on any skipped Cosmos or
SQL conformance test. `AdminService` logs only through the `[LoggerMessage]` partials in
`AdminService.Logging.cs` (append EventIds). ClientApp: Node 22, `npm run lint`, `npm test -- --run`,
`npm run build` (`tsc` excludes `*.test.tsx`, so only vitest catches test type errors). Always
`--include=*.cs` and exclude `/bin/ /obj/` when grepping this repo.

## Design summary (becomes Spec 032)

**What it is.** An Admin → Operations card "Reconcile stale Pending" per endpoint: *Preview* lists
every Pending row written by a request-type message together with a verdict derived from the event's
stored history; *Repair* replaces only the `Repairable` rows with the Completed projection the
Resolver would have written from the stored `ResolutionResponse`, conditionally on the row still being
Pending with the same `LastMessageId`, and writes an audit row per repaired event. It never resubmits,
never skips, never touches history, never invents a status: it restores a decision the Resolver already
recorded. Provider parity (Cosmos, SQL Server, in-memory) is a requirement.

**Why the WebApp.** It runs under the deployed identity (no Cosmos key on a laptop), is gated by the
site-Owner role, is audited under the operator's login, and reaches SQL Server deployments the
Cosmos-only `nb` CLI cannot. Spec 030 §12 item 2 is resolved as "WebApp admin action"; the `nb` verb is
dropped (see Out of scope).

**The rule** (`NimBus.MessageStore.StalePendingReconciler`, pure, in Abstractions next to `StaleWriteGuard`):
- Candidate row: `ResolutionStatus == Pending`, `PendingSubStatus` null, `MessageType` in
  {EventRequest, ResubmissionRequest, SkipRequest, HandoffCompletedRequest, HandoffFailedRequest}
  (Spec 030 §9 step 1; control-written rows are listed so the operator sees them, they are never
  auto-repaired).
- Classification uses only history messages in the row's session (null == empty). Terminals are
  `ResolutionResponse`/`ErrorResponse` with `From` == endpoint (ordinal-ignore-case) plus any
  `SkipResponse`. Verdict ladder, first match wins: `HistoryMissing` (no messages) → `NoTerminal` →
  `LatestTerminalIsError` → `LatestTerminalIsSkip` → `LatestTerminalIsDeadLettered`
  (`DeadLetterErrorDescription != null`; `GetResultingStatus` would say DeadLettered) →
  `ResponseNotBeforeRow` (`response.EnqueuedTimeUtc >= row.EnqueuedTimeUtc`) → `LaterControlMessage`
  (any non-`EventRequest` message enqueued after the response; for a control-written row this is always
  its own copy) → `LaterRequestCopy` (any `EventRequest` enqueued after the row's own `EnqueuedTimeUtc`
  — a stored copy the row does not reflect yet; keyed on time, not on `MessageId`, because 3.7.0
  re-sends keep the id) → `Repairable`.
- Projection: `CreateUnresolvedEvent` field for field from the response (`UpdatedAt = now`,
  `ResolutionStatus = Completed`, `MessageType = ResolutionResponse`, `LastMessageId = response.MessageId`,
  `Reason = response.DeadLetterErrorDescription`, `EndpointId` falls back to the endpoint argument when the
  stored response has none). Handoff wall-clock override when history has a `PendingHandoffResponse`:
  `response.EnqueuedTimeUtc − earliest EventRequest.EnqueuedTimeUtc` (the Resolver uses write time and
  `FirstOrDefault`; documented divergence, deterministic). Throws for anything but a clean
  `ResolutionResponse`.

**The store member** (`IMessageTrackingStore`):
`Task<bool> TryCompletePendingMessage(string eventId, string sessionId, string endpointId, string? expectedLastMessageId, UnresolvedEvent content)`
— replace the row with `content` as the same terminal document `UploadCompletedMessage` writes, only if
the row exists, is Pending, is not soft-deleted and its `LastMessageId` equals `expectedLastMessageId`
(ordinal, null == null). True iff replaced; false for missing / other status / other id / lost
compare-and-swap (no retry: someone else decided, the caller re-previews). Provider failures throw.
Default interface implementation = `GetPendingEvent` + compare + `UploadCompletedMessage` (documented as
the non-atomic compatibility fallback for external providers); the three built-in providers override
atomically. `StaleWriteGuard` is untouched: once the row is Completed it already refuses late request
copies; a later legitimate control request still reopens the row by design.

**API** (tag Admin, site Owner, rate policy inherited):
`POST /api/admin/endpoint/{endpointId}/stale-pending-preview` → `StalePendingPreview`;
`POST /api/admin/endpoint/{endpointId}/stale-pending-reconcile` → `StalePendingReconcileResult`.
Body `StalePendingReconcileRequest { enqueuedBefore?: date-time, maxRows?: int, maxRepairs?: int, note?: string }`.
Preview never writes and is not audited (like the other previews). Reconcile: 400 unless
`enqueuedBefore` is given and ≤ now − 15 min; recomputes the preview server-side (never trusts a client
list); skips rows whose `UpdatedAt` is younger than 15 min; one `ReconcileStalePending` audit row per
invocation (denied and success, `data` = request + counts) plus one per repaired event via
`StoreMessageAudit` (`Comment` = human sentence, `Data` = JSON {previousStatus, newStatus,
staleMessageType, staleMessageId, staleEnqueuedTimeUtc, responseMessageId, responseEnqueuedTimeUtc,
note}). Synchronous, paged internally, capped (`maxRows` default 500, max 2000, `truncated` flag).

**UI.** `StalePendingReconcileCard` in Operations → recovery group: endpoint picker, cut-off
(default now − 1 h, sent as UTC), Preview → tiles (candidates / repairable / operator decision) + row
table (verdict badge, event id, stale id, response id, times, detail) + Download CSV → red
"Repair N rows" → `ConfirmDestructiveAction` (type the endpoint id) → `OperationProgress`, then the
preview refreshes. No per-row action on the endpoint page in v1 (Admin is the site-Owner surface).

**Audit type.** New `MessageAuditType.ReconcileStalePending`, appended last.

## Order of work (each step builds Release and its tests pass before the next)

### 0. Spec 032
- `docs/spec/032-stale-pending-reconcile/spec.md`, header per 030/031 (Status: proposed; Trigger: Spec 030
  §9/§12 item 2 and the EET Nav09Endpoint incident; Baseline: `feat/stale-write-guard` head;
  Review status), sections: Incident recap (one paragraph, link 030 §1–2), Goals, Non-goals (no
  resubmit/skip, no free-form "mark Completed", no terminal-over-terminal repair, no SignalR push),
  Design (the four blocks above, verbatim), Interactions that must keep working (guard refusals after
  repair, `HandoffSettlementService` gate, agent zone reads only `PendingSubStatus = Handoff` rows on
  its own endpoint, ADR-012 settlement projection), Tests (the lists in steps 1–6), Rollout (3.7.0,
  external providers get the default fallback), Residual risks (control-written rows need a human;
  Cosmos Completed rows expire after 30 days; a later DLQ replay of pre-3.7.0 copies would need the
  guard, which is live in the same release), Open decisions (see the last section of this plan),
  Alternatives rejected (Manager control message — no message completes a plain Pending row without the
  subscriber; `nb` verb — Cosmos-only and no audit; Cosmos `PatchItemAsync` + `FilterPredicate` — rejected
  in 030 §13; unconditional `UploadCompletedMessage` after a read — loses the race the whole feature
  exists for).
- Update Spec 030: status line (§9 packaging now Spec 032) and §12 item 2 → resolved.

### 1. The rule, once (Abstractions + pure tests)
- New `src/NimBus.MessageStore.Abstractions/States/StalePendingReconciler.cs` (namespace
  `NimBus.MessageStore`, usings `NimBus.Core.Messages` only, XML docs): `enum StalePendingVerdict`
  (order above), `sealed record StalePendingClassification(UnresolvedEvent Row, StalePendingVerdict Verdict,
  MessageEntity? Response, MessageEntity? LatestTerminal, string Detail) { bool IsRepairable }`,
  `static class StalePendingReconciler { IReadOnlyList<MessageType> CandidateRowTypes; bool IsCandidate(UnresolvedEvent);
  StalePendingClassification Classify(UnresolvedEvent row, IReadOnlyCollection<MessageEntity> history, string endpointId);
  UnresolvedEvent BuildCompletedProjection(MessageEntity response, IReadOnlyCollection<MessageEntity> history, string endpointId, DateTime utcNow) }`.
  Port from the reference `RepairRules.cs` with two changes: the row is the `UnresolvedEvent` (no
  `PendingRow`/ETag), and `LaterRequestCopy` keys on `EnqueuedTimeUtc > row.EnqueuedTimeUtc` only (drop
  the `MessageId` comparison and the "stale copy not found" detail). Sort history by `EnqueuedTimeUtc`
  inside (Cosmos and in-memory return it unordered, SQL ordered).
- Tests first: `tests/NimBus.MessageStore.InMemory.Tests/StalePendingReconcilerTests.cs` (MSTest,
  `[DataRow]` where the reference used `[Theory]`), porting the 24 reference cases: incident signature;
  retry-then-success-then-copy; no response; empty history; error after resolution; skip latest;
  dead-lettered response (+ projection throws); response not before row; resubmission after response;
  each of SkipRequest/Handoff*Request/PendingHandoffResponse/DeferralResponse after the response; later
  request copy by time; other-session response ignored; other-endpoint response ignored; endpoint
  case-insensitive; `CandidateRowTypes` pinned to the spec list; `IsCandidate` rejects
  `PendingSubStatus = "Handoff"` and non-Pending rows; projection field map; `EndpointId` fallback;
  handoff wall-clock; projection refuses `ErrorResponse`. Run red (stubs throw), then implement.

### 2. Store contract: default member, in-memory, conformance
- `IMessageTrackingStore.TryCompletePendingMessage(...)` with the default body
  (`GetPendingEvent(endpointId, eventId, sessionId)` → null or `LastMessageId` mismatch → false →
  `UploadCompletedMessage`), XML doc stating the conditional contract, the fallback's non-atomicity and
  that built-in providers are atomic. Forward it in `InstrumentingMessageTrackingStoreDecorator`
  (`InstrumentAsync(nameof(TryCompletePendingMessage), …)`), `ThrowingStore` (passthrough) and
  `FakeCosmosDbClient` (record + configurable result), facades `CosmosDbClient` / `SqlServerMessageStore`
  (delegation lines; the tracking classes come in step 3 — until then the facades may call the default
  via the interface, or step 2 and 3 land in one build).
- `InMemoryMessageStore`: `TryGetValue(Key(...))`; refuse unless `ResolutionStatus == Pending` and
  `LastMessageId` matches; `Stamp(content, Completed)`; `_events.TryUpdate(key, content, existing)`
  (compare-and-swap by reference) → applied. Keep the existing "Stamp mutates the caller's content"
  convention and say so in a comment.
- Conformance cases in `src/NimBus.Testing/Conformance/MessageTrackingStoreConformanceTests.cs`, one
  endpoint id, distinct event ids, fresh events: replaces a Pending row whose `LastMessageId` matches
  and the row then reads back the way the existing Completed cases assert it (mirror the lookups used
  around lines 179–230 and 459; `DownloadEndpointStateCount(...).PendingCount` drops to 0); refuses on
  `LastMessageId` mismatch (row still Pending via `GetPendingEvent`); refuses when the row is Completed,
  Failed, or missing (false, nothing written); after a successful repair a stale `EventRequest`
  `UploadPendingMessage` is refused by the guard (false) and the row stays Completed; null
  `expectedLastMessageId` matches a null `LastMessageId` only. Red on the in-memory run before the
  store change, green after.

### 3. Cosmos and SQL Server providers
- `CosmosDbMessageTrackingStore.TryCompletePendingMessage`: `ReadItemAsync<EventDbo>(id, pk)` (sees
  soft-deleted docs); 404 → false; refuse unless `Status == PendingStatus && !(Deleted ?? false) &&
  Event.LastMessageId == expected` (ordinal); build the Completed `EventDbo` through a private factory
  shared with `UploadCompletedMessage` (`Deleted = true`, `TimeToLive = 30 d`, `EventType`,
  `SessionId`), then `UpsertItemAsync(dbo, pk, new ItemRequestOptions { IfMatchEtag = current.ETag,
  EnableContentResponseOnWrite = false })`; 412 → `LogInformation("COSMOS COMPLETE-IF-PENDING refused …")`
  → false. Only existing `ICosmosContainerAdapter` members. Unit tests
  `tests/NimBus.MessageStore.CosmosDb.Tests/CosmosDbClientTryCompleteTests.cs` with
  `RecordingCosmosAdapters` (`EnqueueRead` with an ETag): captured `IfMatchEtag` equals the read ETag and
  the upserted document has `status = Completed`, `deleted = true`, `ttl = 2592000`; mismatched
  `LastMessageId` → no upsert, false; scripted 404 → false; scripted 412 → false, single read.
- `SqlServerMessageTrackingStore.TryCompletePendingMessage`: one statement —
  `UPDATE {T("UnresolvedEvents")} SET <the MERGE's UPDATE column list> WHERE EndpointId = @EndpointId AND
  EventId = @EventId AND ((SessionId IS NULL AND @SessionId IS NULL) OR SessionId = @SessionId) AND
  Status = 'Pending' AND Deleted = 0 AND ((LastMessageId IS NULL AND @Expected IS NULL) OR LastMessageId
  COLLATE Latin1_General_BIN2 = @Expected); SELECT @@ROWCOUNT;` via `QuerySingleAsync<int>` (Dapper's
  records-affected is −1 under `SET NOCOUNT ON`, see the comment at `:60-62`), true iff 1. Extract the
  SET list into a private constant shared with `UpsertStatus` so the two cannot drift (`Deleted = 0`
  stays: SQL Completed rows are not soft-deleted). No DbUp migration.
- Run the Cosmos and SQL conformance suites locally with the env vars (emulator: `docker run … azure-cosmos-emulator:vnext-preview --protocol http`, Gateway mode) or state that CI carries them.

### 4. WebApp contract, audit type, service, controller
- `MessageAuditEntity.cs`: append `ReconcileStalePending` after `DeleteStorageContainer` with an XML
  summary; add `reconcileStalePending` to the three `auditType` lists in `api-spec.yaml`;
  `AuditTypeContractTests` stays green.
- `api-spec.yaml`: the two paths/operationIds, schemas `StalePendingReconcileRequest`,
  `StalePendingPreview { endpointId, scanned, candidates, repairable, truncated, rows[] }`,
  `StalePendingRow { eventId, sessionId, eventTypeId, rowMessageType, staleMessageId, rowEnqueuedTimeUtc,
  rowUpdatedAt, verdict (string enum, PascalCase names of StalePendingVerdict), detail, responseMessageId,
  responseEnqueuedTimeUtc }`, `StalePendingReconcileResult { processed, succeeded, failed, skipped,
  errors[], repairedEventIds[] }`; responses 200/400/403/404. Update `docs/webapp-rest-api.md` Admin op
  count. `dotnet build src/NimBus.WebApp -c Release` (without `SkipSpaBuild`) regenerates
  `ApiContract.g.cs` and `index.ts`; commit both.
- `IAdminService`: `Task<StalePendingPreview> PreviewStalePendingAsync(string endpointId, DateTime? enqueuedBefore, int maxRows)`
  and `Task<StalePendingReconcileResult> ReconcileStalePendingAsync(string endpointId, DateTime enqueuedBefore, int? maxRepairs, string auditorName, string? note)`;
  update `ThrowingAdminService` (`AdminStatusSafetyTests.cs:399`) and `StubAdminApi`
  (`RateLimitEnforcementTests.cs:526`).
- New partial `Services/AdminService.Reconcile.cs`: page `GetEventsByFilter(new EventFilter { EndPointId = endpointId,
  ResolutionStatus = ["Pending"] }, token, PageSize)`; keep `StalePendingReconciler.IsCandidate(row)` and
  `row.EnqueuedTimeUtc < enqueuedBefore`; stop at `maxRows` (`truncated = true`); per candidate
  `GetEventHistory(row.EventId)` → `Classify`. Verify while implementing that the Cosmos
  `GetEventsByFilter` projection keeps `MessageType`, `LastMessageId`, `EnqueuedTimeUtc`, `UpdatedAt`,
  `SessionId`, `EventTypeId`, `PendingSubStatus` (`CosmosDbMessageTrackingStore.cs` ~660-700). Reconcile:
  recompute, for each `Repairable` with `UpdatedAt <= now − 15 min` (else `skipped`), projection with
  `now`, `_messageStore.TryCompletePendingMessage(row.EventId, row.SessionId, endpointId, row.LastMessageId, projection)`;
  true → `_messageStore.StoreMessageAudit(eventId, new MessageAuditEntity { AuditorName, AuditTimestamp = now,
  AuditType = ReconcileStalePending, Comment = "…", Data = JSON, EventId, EndpointId }, endpointId, eventTypeId)`,
  `succeeded++`, `repairedEventIds.Add`; false → `skipped++`; exception → `failed++`, `errors.Add($"{eventId}: {ex.Message}")`;
  honour `maxRepairs`. Logs via new `[LoggerMessage]` partials (append EventIds).
- `AdminImplementation.PostAdminStalePendingPreviewAsync` / `PostAdminStalePendingReconcileAsync`:
  owner gate (reconcile also audits the denial with `data = JsonConvert.SerializeObject(body)`),
  `EndpointExists` → 404, reconcile validates `enqueuedBefore` (required, ≤ now − 15 min, else
  `BadRequestObjectResult`), auditor via `AuditLogService.ResolveAuditorName(_context)`, success audit
  with `data = { body, processed, succeeded, failed, skipped }`, `OkObjectResult`. Map
  `StalePendingVerdict` → contract enum by name.
- Tests (`tests/NimBus.WebApp.Tests`, MSTest): `AdminReconcileServiceTests` over `InMemoryMessageStore`
  (`CreateAdminService(store)` as in `AdminStatusSafetyTests.cs:359`): seed the incident shape through
  `StoreMessage` + `UploadPendingMessage` with `MessageType.EventRequest` (the guard lets a first
  request write), preview classifies it `Repairable` and its neighbours (`NoTerminal`,
  `LatestTerminalIsError`, `LaterControlMessage`) correctly; reconcile completes exactly the repairable
  row, writes one `ReconcileStalePending` audit with the expected `Data`, leaves the others Pending,
  `maxRepairs` caps, too-recent rows are skipped, a second run repairs nothing; `truncated` at
  `maxRows`. `AdminReconcileApiTests` constructing `AdminImplementation` directly (pattern
  `AdminCosmosContainerTests.cs:100`): non-owner → 403 + denied audit; unknown endpoint → 404; missing or
  too-recent `enqueuedBefore` → 400 and nothing written; success → one invocation audit row with counts.
  `RateLimitEndpointMetadataTests` and `AuditTypeContractTests` unchanged and green.

### 5. Client
- `ClientApp/src/components/admin/stale-pending-reconcile.tsx` exporting `StalePendingReconcileCard({ endpoints })`,
  modelled on `SubscriptionPurgeCard`: state `preview/result/loading/executing/showConfirm`, endpoint
  picker, cut-off `<Input type="datetime-local">` defaulting to now − 1 h and sent as UTC ISO, Preview
  → `client.postAdminStalePendingPreview(endpointId, new api.StalePendingReconcileRequest({...}))`,
  three `StatTile`s, a plain `<table className="w-full text-sm">` (as `EndpointControlsCard`) with
  `Badge` per verdict (`completed` tone for Repairable, `warning` for operator-decision verdicts),
  `TruncatedGuid` ids, detail in `font-mono text-[11.5px]`, "Download CSV" building a blob from the
  preview rows, red `Button` "Repair N rows" (disabled when N = 0) → `ConfirmDestructiveAction`
  (`confirmText = endpointId`, a note field) → `client.postAdminStalePendingReconcile(...)` →
  `OperationProgress` (processed/succeeded/failed/errors) and re-run the preview. Surface a 400 from the
  server (too-recent cut-off) as an error toast. Register in `operations.tsx` recovery group and bump
  its `count`.
- `stale-pending-reconcile.test.tsx` (vitest, FakeClient pattern): preview renders tiles and verdict
  badges from a stubbed `StalePendingPreview`; Repair is disabled at 0 repairable; confirm → apply called
  with the endpoint id and the request; result renders; a `SwaggerException` 400 shows the error. Run
  `npm run lint`, `npm test -- --run`, `npm run build`.

### 6. Docs
- New `docs/stale-pending-reconcile.md` operator guide (first Operations-tab guide; structure of
  `docs/heartbeat.md`): what the card does, "Access is site **Owner**, same as the rest of `/admin`.
  Every repair is audited as `ReconcileStalePending` (one row per run, one per repaired event)",
  Verdict | Meaning | What to do table, the procedure (preview → CSV → repair, cut-off ≥ 15 min old),
  Cautions (control-written rows, dead-lettered responses, Cosmos 30-day expiry of Completed rows,
  re-run preview after any DLQ replay), Related (030 flows, subscriptions guide). Index it in
  `CLAUDE.md` and `AGENTS.md` (`docs/` tree) and cross-link from
  `docs/service-bus-subscription-admin.md` Related.
- `docs/storage-providers.md`: under the guard section, the one conditional terminal primitive
  (`TryCompletePendingMessage`), its default fallback, and that the conformance suite pins it.
  `docs/message-flows.md` "Which messages may reopen a settled row": one sentence that rows corrupted
  before 3.7.0 are repaired from Admin → Operations. `docs/webapp-rest-api.md`: Admin tag count and an
  "Actions audited" row (`ReconcileStalePending` → `AdminImplementation.PostAdminStalePendingReconcileAsync`).
  `docs/features.md` Operations UI row mentions the repair. `docs/architecture.md` §4: one sentence
  that the reconcile action is the single operator-initiated terminal write and why it is safe.
- ADR-002 (Centralized Resolver): append `## Note — 2026-09-… — Operator reconcile` recording the one
  sanctioned non-Resolver terminal write: it only re-applies a `ResolutionResponse` the Resolver already
  stored, conditionally on the row's `LastMessageId`, audited, site-Owner only. No new ADR (amendments
  are the repo's convention; CONTRIBUTING wants the storage-abstraction change discussed, which Spec 032
  plus this note provide).
- Spec 032 status → implemented; Spec 030 status line and §12 item 2 updated (step 0 wrote the wording).

## Verification
- `cd C:\Git\NimBus\.worktrees\stale-pending-reconcile && dotnet build src/NimBus.sln -c Release`
- `dotnet test tests/NimBus.MessageStore.InMemory.Tests -c Release` (rule tests + conformance)
- `dotnet test tests/NimBus.MessageStore.CosmosDb.Tests -c Release` with `NIMBUS_COSMOS_TEST_CONNECTION`
  and `NIMBUS_COSMOS_TEST_GATEWAY=1` against the vnext emulator; `dotnet test tests/NimBus.MessageStore.SqlServer.Tests -c Release`
  with `NIMBUS_SQL_TEST_CONNECTION`; otherwise rely on CI's must-not-skip gates and say so
- `dotnet test tests/NimBus.OpenTelemetry.Tests tests/NimBus.Resolver.Tests tests/NimBus.WebApp.Tests -c Release`
- `dotnet build src/NimBus.WebApp -c Release` (regenerates contract + client; `git status` shows only the
  intended generated diffs), then `npm --prefix src/NimBus.WebApp/ClientApp run lint`,
  `npm --prefix src/NimBus.WebApp/ClientApp test -- --run`, `npm --prefix src/NimBus.WebApp/ClientApp run build`
- Manual: `dotnet run --project src/NimBus.AppHost -- --UseEmulator true`, seed one endpoint with the
  incident shape (publish, complete, then `UploadPendingMessage` a request copy through a small test
  hook or the in-memory seed used by the tests), preview shows `Repairable`, repair flips it, the Audit
  tab shows `ReconcileStalePending`, a second preview shows nothing repairable
- `/code-review feat/stale-write-guard..feat/stale-pending-reconcile high` before the PR

## Commits (local only; no push)
1. `docs(spec): Spec 032 stale-Pending reconcile from the WebApp` (step 0)
2. `feat(store): add StalePendingReconciler and the conditional TryCompletePendingMessage contract` (steps 1–2)
3. `feat(store): atomic TryCompletePendingMessage in the Cosmos and SQL Server providers` (step 3)
4. `feat(webapp): stale-Pending reconcile admin API with per-event audit` (step 4)
5. `feat(webapp-ui): reconcile stale Pending card in Admin → Operations` (step 5)
6. `docs: stale-Pending reconcile operator guide, provider contract and ADR-002 note` (step 6)

## Out of scope
- The `nb container reconcile-stale-pending` verb: the CLI is Cosmos-only by construction and `nb
  container skip` writes no audit row; if wanted later it reuses `StalePendingReconciler` and the new
  store member (Spec 030 §12 item 2 closes as "WebApp action").
- SignalR push after admin store writes and `IStoreResultCache` invalidation: no admin mutation does it
  today; a shared broadcaster for purge/skip/reconcile is a separate follow-up.
- A per-row "Reconcile" action on the endpoint page and any Contributor-level access: v1 is a
  site-Owner Admin card like every other store-mutating operation.
- Terminal-over-terminal repairs (Spec 030 §4) and rows whose history was purged.
- The EET Nav09Endpoint production repair itself: prod runs 3.5.1/3.6.1 without this API, so the
  EET.Deploy console tool remains the immediate path; the WebApp action covers everything from 3.7.0 on.

## Open decisions for the repo owner
1. Verdict exposure: PascalCase enum names on the wire (as `ResolutionStatus`) vs camelCase (as the
   audit types). The plan assumes PascalCase.
2. Whether the per-event audit should also carry the operator `note` in `Comment` or only in `Data`.
3. `maxRows` default 500 / cap 2000 for the synchronous preview; raise or add paging if a backlog larger
   than that must be previewed at once.
4. Whether to expose the preview through the MCP server (`NimBus.Mcp` today exposes agent tools only,
   no admin actions); the plan does not.
