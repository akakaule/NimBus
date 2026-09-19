# Spec 032 — Repairing stale Pending rows from the WebApp

Status: implemented (2026-09-17, branch `feat/stale-pending-reconcile`).
Trigger: Spec 030 §9 (the repair procedure) and §12 item 2 (packaging, left open). The rows the
EET `Nav09Endpoint` incident corrupted on 2026-09-14 still read `Pending`; Spec 030 stops new ones
appearing but repairs none.
Baseline: branch `feat/stale-write-guard` (Spec 030 implemented, NimBus 3.7.0 line).
Reference implementation: `C:\Git\EET\EET.Deploy\ops\stale-pending-repair\` (branch
`ops/stale-pending-repair`, commit `f9aaee9`) — a Cosmos-only console tool implementing §9 exactly,
with 24 tests and an emulator run. Its `RepairRules.cs` is the rule this spec ports into the
product; its `CosmosRepairStore.ApplyAsync` is the Cosmos write.
Companion: Spec 030 (the guard). The guard keeps a repaired row repaired — once the row is
Completed it already refuses the late request copies that corrupted it.

## 1. Incident recap

Spec 030 §1–§2 has the detail. In short: every status write was last-writer-wins, and a request
copy delayed behind its own outcome — by auto-forward lag or by a `ScheduleRedelivery` re-send —
replaced the settled row with `Pending`. About 100 `AccountUpdated` events on `Nav09Endpoint` show
`ResolutionStatus = Pending` although the NAV adapter completed them and the Resolver recorded the
`ResolutionResponse` in the per-message history.

The outcome is therefore not lost. It is in the messages container, one document per message, and
the Flow tab still shows it. Only the projection onto the audit row was overwritten. Repair means
re-applying a decision the Resolver already made, not inventing one.

## 2. Goals

1. An operator can see, per endpoint, every Pending row that a request-type message wrote, with a
   verdict derived from that event's stored history.
2. Rows whose history proves the incident signature can be repaired to Completed — as the exact
   document the Resolver would have written — conditionally on the row not having changed.
3. Everything else is listed and left to a human. The tool never guesses a status.
4. Provider parity: Cosmos DB, SQL Server and the in-memory store behave identically, pinned by
   the conformance suite.
5. Every repair is attributable: audited under the operator's login, per run and per event.

## 3. Non-goals

- No resubmit and no skip. Both re-run work the adapter already did (§9 of Spec 030).
- No free-form "mark this row Completed". The only terminal the action can write is one the
  Resolver already stored as a `ResolutionResponse` for that event and session.
- No terminal-over-terminal repair (Spec 030 §4 keeps those last-writer-wins) and no repair of an
  event whose history has been purged.
- No SignalR push after the write. No admin mutation does that today (purge, skip, delete are all
  silent); a shared broadcaster is a separate follow-up.
- No `nb` CLI verb. Spec 030 §12 item 2 closes as "WebApp admin action" — see §9.

## 4. Design

### 4.1 Where it lives

An Admin → Operations card, "Reconcile stale Pending", per endpoint. **Preview** lists every
candidate row with a verdict; **Repair** replaces only the `Repairable` ones.

Why the WebApp rather than the CLI: it runs under the deployed identity (no Cosmos key on a
laptop), it is gated by the site-Owner role, it is audited under the operator's login, and it
reaches SQL Server deployments, which the Cosmos-only `nb` CLI cannot.

### 4.2 The rule — `NimBus.MessageStore.StalePendingReconciler`

Pure, in `NimBus.MessageStore.Abstractions` next to `StaleWriteGuard`, so the WebApp, the
conformance suite and any future CLI share one implementation.

**Candidate row:** `ResolutionStatus == Pending`, `PendingSubStatus` null (a parked handoff is
genuinely in flight), and `MessageType` in `{EventRequest, ResubmissionRequest, SkipRequest,
HandoffCompletedRequest, HandoffFailedRequest}` — Spec 030 §9 step 1. Control-written rows are
listed so the operator sees them; they are never auto-repaired, because the later control message
that wrote such a row is itself the thing that would have to be judged.

**Classification** uses only history messages in the row's session (null session == empty) that
concern the endpoint — `MessageEntity.EndpointId` equals it, ordinal-ignore-case; the Resolver
stamps that as `To` for requests and `From` for responses — plus any `SkipResponse` regardless of
sender. The endpoint scope matters in a fan-out: every subscriber's request copy and response share
the event id *and* the publisher's session id, and without it a sibling's response would read as a
later control message and its request copy as a later request copy, so no fan-out event could be
repaired. A message with no stamped `EndpointId` stays in scope (it can only add blockers).
Terminals are `ResolutionResponse` / `ErrorResponse` whose `From` is the endpoint, plus any
`SkipResponse` — a skip is issued on the operator's behalf, so its `From` is not the endpoint, but
it still means the row must not become Completed. First match wins:

| Verdict | Meaning |
|---|---|
| `HistoryMissing` | Nothing stored for the event. |
| `NoTerminal` | No terminal response in the session: genuinely in flight. |
| `LatestTerminalIsError` | Latest terminal is an `ErrorResponse` — operator decision. |
| `LatestTerminalIsSkip` | The row should read Skipped, not Completed. |
| `LatestTerminalIsDeadLettered` | The `ResolutionResponse` carries a non-empty `DeadLetterErrorDescription`; `ResolverService.GetResultingStatus` projects that as DeadLettered, never Completed. Null and empty are the same thing here: the SQL Server provider reads a NULL column back as `string.Empty`, Cosmos and in-memory as null. |
| `ResponseNotBeforeRow` | `response.EnqueuedTimeUtc >= row.EnqueuedTimeUtc` — the row is a newer attempt, still in flight. |
| `LaterControlMessage` | A non-`EventRequest` message follows the response. For a control-written row this is always its own copy, which is why those rows land here by design. |
| `LaterRequestCopy` | An `EventRequest` enqueued after the row's own `EnqueuedTimeUtc` is stored but not projected yet; repairing would only be undone. |
| `Repairable` | The incident signature: a clean `ResolutionResponse` precedes the request copy that wrote the row, and nothing but request copies follow it. |

`LaterRequestCopy` keys on time alone. The reference tool also compared `MessageId`, because
pre-3.7.0 re-sends minted a fresh GUID; Spec 030 §5.7 made `ScheduleRedelivery` keep the original
id, so a stored copy can legitimately share the row's `LastMessageId`. Comparing ids would
therefore miss exactly the copies 3.7.0 produces.

**Projection.** `BuildCompletedProjection` rebuilds the `UnresolvedEvent` field for field as
`ResolverService.CreateUnresolvedEvent` would from the stored response: `UpdatedAt = now`,
`ResolutionStatus = Completed`, `MessageType = ResolutionResponse`,
`LastMessageId = response.MessageId`, `Reason = response.DeadLetterErrorDescription`, and
`EndpointId` falling back to the endpoint argument when the stored response carries none. It
throws for anything but a clean `ResolutionResponse`.

One documented divergence: when the event went through an async handoff, the Resolver stamps a
wall-clock span measured at *write* time (`ComputeHandoffWallClockMsIfTerminal`). A repair cannot
reproduce that, so it uses `response.EnqueuedTimeUtc − earliest EventRequest.EnqueuedTimeUtc`,
which is deterministic and is the faithful stand-in.

### 4.3 The store primitive — `TryCompletePendingMessage`

```csharp
Task<bool> TryCompletePendingMessage(
    string eventId, string sessionId, string endpointId,
    string? expectedLastMessageId, UnresolvedEvent content);
```

Replaces the row with `content`, as the same terminal document `UploadCompletedMessage` writes,
**only if** the row exists, is Pending, is not soft-deleted, and its `LastMessageId` equals
`expectedLastMessageId` (ordinal, null == null). `true` iff replaced. `false` for missing, other
status, other id, or a lost compare-and-swap — no retry, because someone else decided in the
meantime and the operator should re-preview. Provider failures throw.

`IMessageTrackingStore` had no conditional terminal write; `UploadCompletedMessage` is
unconditional on every provider. The default interface implementation is `GetPendingEvent` +
compare + `UploadCompletedMessage`, documented as the non-atomic compatibility fallback for
external providers; the three built-in providers override it atomically (Cosmos `IfMatchEtag`,
SQL a single guarded `UPDATE`, in-memory a reference CAS). Default interface members are the
repo's precedent for non-breaking additions (`GetResubmitCounts`, `SetEventReport`).

`StaleWriteGuard` is untouched. Once the row is Completed the guard already refuses late request
copies, and a later legitimate control request still reopens it — by design.

### 4.4 API

Tag `Admin`, site Owner, inheriting the `nimbus-admin` rate policy:

- `POST /api/admin/endpoint/{endpointId}/stale-pending-preview` → `StalePendingPreview`
- `POST /api/admin/endpoint/{endpointId}/stale-pending-reconcile` → `StalePendingReconcileResult`

Preview never writes and is not audited, like the other previews. Reconcile **recomputes the
preview server-side** and never trusts a client-supplied list. It requires `enqueuedBefore` and
rejects a cut-off newer than 15 minutes ago, and it skips any row whose `UpdatedAt` is younger
than 15 minutes — two independent guards against repairing something still moving.

Auditing: one `ReconcileStalePending` row per invocation (on the denied branch too), plus one per
repaired event through `StoreMessageAudit`, carrying a human `Comment` and a JSON `Data` with the
previous status, the stale message and the response that replaced it.

### 4.5 UI

`StalePendingReconcileCard` in Operations → recovery: endpoint picker, cut-off (default now − 1 h,
sent as UTC), Preview → three tiles (candidates / repairable / operator decision) + a row table
with a verdict badge and a Download CSV, then a red "Repair N rows" behind
`ConfirmDestructiveAction` (type the endpoint id) and `OperationProgress`. No per-row action on the
endpoint page in v1: Admin is the site-Owner surface, as for every other store-mutating operation.

## 5. Interactions that must keep working

| Flow | Why it still works |
|---|---|
| A late request copy arriving after a repair | The row is Completed, so `StaleWriteGuard` refuses it (Spec 030 §5.1). |
| A legitimate Resubmit or Skip after a repair | Control requests reopen a settled row by design; the repair changes nothing about that. |
| `HandoffSettlementService.SettleAsync` | Gated on `PendingSubStatus == "Handoff"`, and parked rows are never candidates. |
| The agent zone's receive loop | Reads only `PendingSubStatus = "Handoff"` rows on its own endpoint; candidates have a null sub-status. |
| ADR-012 settlement projection | Settlement requests still project plain Pending rows; those rows are *listed* as candidates and land on `LaterControlMessage`, never repaired automatically. |
| The Resolver | Unchanged. No Resolver code is touched by this spec. |

## 6. Tests

- **Rule** (`StalePendingReconcilerTests`, in-memory test project): the 24 cases ported from the
  reference — incident signature, retry-then-success-then-copy, no response, empty history, error
  after resolution, skip latest, dead-lettered response, response not before row, resubmission
  after response, each non-request type after the response, later request copy by time,
  other-session and other-endpoint responses ignored, endpoint case-insensitivity,
  `CandidateRowTypes` pinned, `IsCandidate` rejecting parked and non-Pending rows, the projection
  field map, the `EndpointId` fallback, the handoff wall-clock, and the projection refusing an
  `ErrorResponse`.
- **Store contract** (conformance suite, so all three providers): replaces a matching Pending row;
  refuses on `LastMessageId` mismatch, on a non-Pending row, and on a missing row; a null
  `expectedLastMessageId` matches only a null id; after a repair a stale `EventRequest` write is
  refused by the guard and the row stays Completed.
- **Cosmos unit tests**: the captured `IfMatchEtag` equals the read ETag, the upserted document has
  `status = Completed`, `deleted = true`, `ttl = 2592000`; mismatch, 404 and 412 all return false
  without writing.
- **WebApp**: service tests over the real in-memory store (preview classifies the incident shape and
  its neighbours; reconcile completes exactly the repairable row, audits it, caps at `maxRepairs`,
  skips too-recent rows, and a second run repairs nothing) and API tests for 403 + denied audit,
  404, 400 on a missing or too-recent cut-off, and the success audit.
- **Client**: preview renders tiles and badges, Repair is disabled at zero, confirm calls the API,
  a 400 surfaces as an error.

## 7. Rollout

No migration, no topology change. Ships with 030/031 as 3.7.0 — a new `IMessageTrackingStore`
member is a minor bump, and four docs on the guard branch already say "Since 3.7.0".

External `IMessageTrackingStore` implementations keep compiling and get the non-atomic default
fallback; they should override it. The WebApp deploys on its normal cadence — nothing else needs
to move first, because the action only reads and writes the audit store.

## 8. Residual risks

- **Control-written rows always need a human.** A row written by a rescheduled `ResubmissionRequest`
  or handoff settlement lands on `LaterControlMessage` and is listed, never repaired.
- **Cosmos Completed rows expire after 30 days.** A repaired row inherits the same 30-day TTL, and a
  row whose event has aged out of the messages container classifies as `HistoryMissing`.
- **A dead-lettered response cannot be repaired.** `LatestTerminalIsDeadLettered` is a real state,
  not an edge case: the operator decides between resubmit and skip.
- **A DLQ replay of a pre-3.7.0 copy** could still corrupt a repaired row if the guard were not
  live — it ships in the same release, so it is.
- **The preview is synchronous and capped** (`maxRows` default 500, max 2000, `truncated` flag). A
  backlog larger than the cap needs several passes with a moving cut-off.

## 9. Open decisions

1. ~~Verdict on the wire: PascalCase or camelCase.~~ **Resolved** 2026-09-17: PascalCase, matching
   `ResolutionStatus`; `StalePendingRowVerdict` mirrors `StalePendingVerdict` by name and a test
   pins the two enums to each other.
2. Whether the per-event audit carries the operator note in `Comment` as well as `Data`.
3. `maxRows` default 500 / cap 2000, or paging for larger backlogs.
4. Whether to expose the preview through the MCP server. `NimBus.Mcp` exposes agent tools only
   today; this spec does not add admin actions to it.

## 10. Alternatives rejected

- **A Manager control message that completes the row.** Nothing in the system completes a plain
  Pending row without the subscriber: `HandoffCompletedRequest` is Manager → subscriber and the
  subscriber requires the session to be blocked by that event; `HandoffSettlementService` 400s on a
  row with a null `PendingSubStatus`. A new message type would mean a new broker path whose only
  purpose is to bypass the adapter.
- **The `nb container reconcile-stale-pending` verb** (Spec 030 §12 item 2). The CLI is Cosmos-only
  by construction, needs an account key on the operator's machine, and `nb container skip` writes no
  audit row. If it is ever wanted it reuses this rule and this store member.
- **Cosmos `PatchItemAsync` with a `FilterPredicate`.** Rejected in Spec 030 §13 — it needs new
  adapter overloads and is unproven on the emulator CI depends on.
- **An unconditional `UploadCompletedMessage` after reading the row.** That is the read-then-write
  race the entire feature exists to avoid; it would reintroduce Spec 030's bug in the repair tool.
- **Repairing from the Flow tab per row.** A per-row action spreads a store-mutating operation
  across a Contributor-visible surface. v1 keeps it where every other destructive operation lives.
