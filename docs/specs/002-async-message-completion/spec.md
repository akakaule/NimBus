# Feature Specification: Async Message Completion via PendingHandoff

Feature Branch: `002-async-message-completion`
Created: 2026-05-04
Updated: 2026-05-07
Status: Implemented (see ADR-012; shipped via #29 / #31 / #36 / #37 / #39). Timeout sweeper (FR-050..FR-053) deferred — design recorded, build follow-up.
Input: User description: "A NimBus subscriber adapter integrates with Microsoft Dynamics 365 F&O via the DMF API. Inserts are asynchronous: the adapter triggers an import job, and the per-entity outcome only arrives later when the status checker polls DMF. We need to settle the Service Bus message immediately, keep the session blocked so siblings on that session don't overtake the in-flight import, and have the audit trail report Pending — not Failed — until the external work reports back."

## Problem (resolved)

NimBus today resolves a message at the moment the handler returns. That works when the handler is the unit of work. It does not work when the unit of work is an external long-running job, because:

1. The Service Bus peek-lock cannot be held for the duration (max ~5 min; F&O DMF jobs frequently exceed this), so the inbound message must be settled promptly.
2. Sibling messages on the same session must defer until the external work reports back, otherwise per-session ordering breaks.
3. Settling as Completed lies about the state (work has not happened yet); settling as Failed lies the other way and pollutes dashboards.
4. The only existing way to keep a session blocked is to throw an exception from the handler — using exceptions for happy-path control flow is an anti-pattern that breaks middleware, telemetry filters, and reasonable code review.

This feature introduces a new outcome — **PendingHandoff** — that maps to the existing `ResolutionStatus.Pending` (with a sub-status), reuses the existing session-blocking machinery, and is signalled by an explicit method on the handler context rather than an exception.

## Scope (resolved)

In scope:
- A new handler outcome `PendingHandoff` signalled by a method on `IEventHandlerContext`.
- Three new `MessageType` values (`PendingHandoffResponse`, `HandoffCompletedRequest`, `HandoffFailedRequest`).
- Two new `IManagerClient` methods (`CompleteHandoff`, `FailHandoff`) that drive settlement without re-invoking the user handler.
- Sub-status fields on `MessageEntity` / `UnresolvedEvent` so the WebApp can distinguish "Pending — Handoff" from ordinary Pending.
- Optional Resolver-side timeout sweeper.
- Documentation, conformance tests, WebApp badge, ADR.

Out of scope:
- Adapter-side concerns (correlation store, status checker, DMF integration). These are described as the motivating use case but are not part of the framework feature.
- A general-purpose external-completion API beyond `CompleteHandoff` / `FailHandoff`.
- Cross-session ordering. Sessions remain independent by design.
- Cosmos / SQL data migration. New fields are nullable; existing rows are unaffected.
- WebApp information-architecture changes beyond rendering the new sub-status.

## User Scenarios & Testing

### User Story 1 - Handler signals async handoff via the context (Priority: P1)

As an adapter author, I want my event handler to signal "this work is in flight on an external system" by calling a method on the handler context — not by throwing an exception — so that my code reads as a normal happy path and middleware does not mistake it for a fault.

Why this priority: Core developer-experience value. Without it, adapter authors fall back to abusing the failure path, which is the status quo we are trying to leave behind.

Independent Test: Write an event handler that calls `ctx.MarkPendingHandoff(reason, externalJobId, expectedBy)` and returns. Assert that no exception is thrown, that `StrictMessageHandler` settles the inbound Service Bus message, that the Resolver records `ResolutionStatus.Pending` with `PendingSubStatus="Handoff"`, and that the session is blocked.

Acceptance Scenarios:

1. Given a handler that calls `ctx.MarkPendingHandoff(...)` and returns, When the message is processed, Then the framework sends `PendingHandoffResponse` to the Resolver, calls `BlockSession`, completes the inbound, and does not send `ResolutionResponse`.
2. Given a handler that returns without calling `ctx.MarkPendingHandoff`, When the message is processed, Then existing behaviour is unchanged (`ResolutionResponse` is sent and the message is recorded as Completed).
3. Given a handler that calls `ctx.MarkPendingHandoff(...)` and then throws an unrelated exception, When the message is processed, Then the existing failure path takes precedence (`ErrorResponse`, `BlockSession`, retry policy if applicable). PendingHandoff metadata is not emitted.
4. Given a handler that calls `ctx.MarkPendingHandoff(...)` twice, When the message is processed, Then the second call wins (idempotent overwrite). No exception is thrown.

---

### User Story 2 - Sibling messages defer while a handoff is in flight (Priority: P1)

As a NimBus operator, I want messages on the same Service Bus session as an in-flight handoff to be parked on the Deferred subscription in FIFO order, so that ordering is preserved across the duration of the external job.

Why this priority: Without it, the feature does not solve the original problem.

Independent Test: Send three messages with the same `SessionId`. The first triggers a `PendingHandoff`. Verify the second and third are received but parked on the Deferred subscription, that the Resolver records them as `Deferred`, and that they are not delivered to the user handler until the session unblocks.

Acceptance Scenarios:

1. Given a session blocked by PendingHandoff, When subsequent messages on the same session arrive, Then they throw `SessionBlockedException` internally and are routed to the Deferred subscription with the existing `DeferralSequence` ordering.
2. Given the session is later settled via `CompleteHandoff`, When `ContinueWithAnyDeferredMessages` runs, Then the deferred siblings are replayed to the main topic in FIFO order, exactly as today's resubmit/skip flow.
3. Given the session is settled via `FailHandoff`, When the operator skips the failed event, Then the existing skip → unblock → replay flow works without modification.

---

### User Story 3 - Audit row reports Pending (not Failed) (Priority: P1)

As an operator, I want a message awaiting an external system to appear under "Pending" in the WebApp, with a sub-status making it clear that it is waiting on an external job, so that dashboard counts and alerting are not polluted by healthy in-flight workloads.

Why this priority: Sets the audit-trail contract that the rest of the design hinges on.

Independent Test: Trigger a PendingHandoff and inspect the resulting `MessageEntity` and `UnresolvedEvent`. Verify `ResolutionStatus = Pending`, `PendingSubStatus = "Handoff"`, `HandoffReason` matches the reason argument, `ExternalJobId` matches if supplied, and `ExpectedBy` matches if supplied.

Acceptance Scenarios:

1. Given a `PendingHandoffResponse` arrives at the Resolver, When the projection is written, Then `ResolutionStatus` is `Pending` and `PendingSubStatus` is `"Handoff"`.
2. Given a Pending+Handoff entry exists, When the WebApp dashboard renders endpoint counts, Then the entry contributes to the Pending count, not the Failed count.
3. Given the WebApp message detail page is opened on a Pending+Handoff entry, When the page renders, Then `HandoffReason`, `ExternalJobId`, and `ExpectedBy` are visible to the operator.
4. Given existing audit rows from before this feature, When the WebApp renders them, Then they continue to render unchanged (the new fields are nullable; legacy `Pending` rows have `PendingSubStatus = null`).

---

### User Story 4 - Settle a handoff terminally without re-invoking the handler (Priority: P1)

As an integration author, I want to settle a PendingHandoff message as Completed or Failed directly — without paying for an extra Service Bus delivery and an extra handler invocation just to obtain a `ResolutionResponse` — so that the cost per settled event is minimal and the audit trail is honest.

Why this priority: Without dedicated terminal commands, callers misuse `ManagerClient.Resubmit` (semantic mismatch + wasted handler runs + double-counted middleware metrics + mislabelled audit rows). This is the central design choice of the feature.

Independent Test: From a status-checker stand-in, call `ManagerClient.CompleteHandoff(pendingEntry, endpoint)`. Assert that the user's `IEventHandler<TEvent>.Handle` is **not invoked**, that the Resolver flips Pending → Completed, that the session unblocks, and that any deferred siblings replay.

Acceptance Scenarios:

1. Given a Pending+Handoff message and a registered handler, When `ManagerClient.CompleteHandoff(...)` is called, Then the handler's `Handle` method is not called. The Resolver records Completed; the session unblocks; deferred siblings replay in FIFO order.
2. Given a Pending+Handoff message, When `ManagerClient.FailHandoff(entity, endpoint, errorText, errorType)` is called, Then the Resolver records Failed with `errorText` preserved verbatim on the audit row, and the session stays blocked.
3. Given a Pending+Handoff message and the operator instead clicks Resubmit in the WebApp, When `ManagerClient.Resubmit` is called, Then the existing flow runs: handler is invoked again, ResolutionResponse fires on success. (Operator-initiated resubmit must remain available as an explicit override.)
4. Given any other state (Completed, Failed, Skipped, DeadLettered), When `CompleteHandoff` or `FailHandoff` is called, Then the framework returns a clear error or no-op (decided in design phase). The state machine MUST NOT be corrupted.

---

### User Story 5 - Operator override unchanged (Priority: P2)

As an operator, I want existing Resubmit and Skip buttons in the WebApp to keep working on Pending+Handoff entries, so that I can intervene manually when the status checker is stuck or wrong.

Why this priority: Trust in the platform requires that operators retain manual control.

Independent Test: From the WebApp, click Resubmit and Skip on a Pending+Handoff entry; verify they take the same code path as today (`HandleResubmissionRequest` / `HandleSkipRequest`).

Acceptance Scenarios:

1. Given a Pending+Handoff entry, When an operator clicks Resubmit, Then `ManagerClient.Resubmit` runs unchanged, the handler is invoked, and the message resolves through the existing flow.
2. Given a Pending+Handoff entry, When an operator clicks Skip, Then `ManagerClient.Skip` runs unchanged, the message moves to Skipped, and deferred siblings replay.

---

### User Story 6 - Optional timeout sweeper (Priority: P2)

As an operator, I want PendingHandoff entries that exceed their `ExpectedBy` deadline to be flipped to Failed automatically with a synthetic timeout error, so that silent backlogs do not accumulate.

Why this priority: Important for production hygiene, but adapter authors must opt in — different integrations have very different reasonable wait times.

Independent Test: Set `ExpectedBy` to a short deadline; trigger a PendingHandoff and let it expire without settlement; verify the Resolver-side sweeper flips it to Failed with `ErrorType = "TimeoutExpired"`. Verify that operators can Resubmit / Skip the resulting Failed entry as usual.

Acceptance Scenarios:

1. Given a Pending+Handoff entry whose `ExpectedBy` has passed, When the sweeper runs, Then the entry is flipped to Failed with a synthetic `TimeoutExpiredError`. Session unblocks via the standard skip/resubmit path.
2. Given a Pending+Handoff entry with no `ExpectedBy`, When the sweeper runs, Then the entry is left untouched.
3. Given the sweeper is disabled in configuration, When PendingHandoff entries age beyond `ExpectedBy`, Then nothing happens (opt-in behaviour).

---

## Edge Cases

- A handler that calls `ctx.MarkPendingHandoff(...)` and then throws — exception path wins; PendingHandoff metadata is discarded.
- A handler that calls `ctx.MarkPendingHandoff(...)` from inside a pipeline middleware — supported. Middleware that observes `ctx.Outcome == PendingHandoff` after the handler returns can branch on it (e.g., metrics).
- `CompleteHandoff` / `FailHandoff` called for an event that is not in PendingHandoff state — must not corrupt state. Either no-op or explicit error (decided in design).
- `CompleteHandoff` / `FailHandoff` called for an event the caller did not block (different `BlockedByEventId`) — analogous to today's `VerifySessionIsBlockedByThis` check. Must reject.
- A session is blocked by a real handler failure (not PendingHandoff). `CompleteHandoff` MUST NOT settle it, since the original failure was not external.
- `MarkPendingHandoff` is called with `expectedBy = null` — sweeper does not apply; entry can stay Pending+Handoff indefinitely (operator manages).
- Two messages on the same session both want PendingHandoff. Only the first one's call to `MarkPendingHandoff` blocks the session; the second one's session-blocked check fires before the user handler is invoked, so it parks on Deferred (existing flow).
- The Resolver receives `PendingHandoffResponse` for an event id it has never seen. Must record the projection with status Pending — same as receiving an `EventRequest` first, but with the sub-status fields populated.
- A `HandoffCompletedRequest` arrives at a subscriber that does not have the corresponding event in flight (e.g., after a redeploy that lost session state). Must be authorized via `AuthorizeManagerRequest`, must verify session blocked-by-this; if mismatched, behaves like a stale Skip.
- The WebApp message detail page on a legacy Pending entry (no sub-status) — must continue to render as before, with the new fields hidden.

## Requirements

### Functional Requirements

#### Handler API & SDK

- FR-001: `IEventHandlerContext` (in `NimBus.SDK.EventHandlers`) MUST expose a new method:
  - `void MarkPendingHandoff(string reason, string externalJobId = null, TimeSpan? expectedBy = null)`
- FR-002: `IEventHandlerContext` MUST expose readable state for the recorded outcome (e.g., `HandlerOutcome Outcome` and a `HandoffMetadata` accessor returning `reason`, `externalJobId`, `expectedBy`). State MUST be inspectable from pipeline middleware after the handler returns.
- FR-003: `MarkPendingHandoff` MUST be idempotent. The last call wins. No exception is thrown on repeated calls.
- FR-004: `EventHandlerContext` (the concrete implementation) MUST be constructed by `EventJsonHandler` such that `StrictMessageHandler` can read the outcome state after the user handler returns. Implementation choice (back-reference to `IMessageContext` vs. shared mutable state container) is decided in design.
- FR-005: The handler MUST NOT be required to throw to signal PendingHandoff. The existing `SessionBlockedException` / `EventContextHandlerException` paths remain reserved for actual session-blocking and actual faults.

#### Message types & Core flow

- FR-010: `MessageType` MUST gain three values:
  - `PendingHandoffResponse` — subscriber → Resolver (response)
  - `HandoffCompletedRequest` — Manager → subscriber (control message)
  - `HandoffFailedRequest` — Manager → subscriber (control message)
- FR-011: `IResponseService` / `ResponseService` MUST gain a `SendPendingHandoffResponse` method modelled on `SendDeferralResponse`. Output message MUST carry `HandoffReason`, `ExternalJobId`, `ExpectedBy`.
- FR-012: `StrictMessageHandler.HandleEventRequest` MUST inspect `ctx.Outcome` after `HandleEventContent` returns successfully. If the outcome is `PendingHandoff`, it MUST:
  1. Send `PendingHandoffResponse` to the Resolver.
  2. Call `messageContext.BlockSession` (same call path as the failure flow).
  3. Call `messageContext.Complete`.
  4. NOT send `ResolutionResponse`.
  5. NOT invoke retry policy.
- FR-013: `StrictMessageHandler` MUST gain a `HandleHandoffCompletedRequest` method analogous to `HandleSkipRequest`. It MUST authorize the Manager origin, verify the session is blocked by this event id, unblock the session, replay deferred messages, send `ResolutionResponse`, and complete the inbound. It MUST NOT invoke the user handler.
- FR-014: `StrictMessageHandler` MUST gain a `HandleHandoffFailedRequest` method. It MUST authorize the Manager origin, verify the session is blocked by this event id, send `ErrorResponse` carrying the supplied `errorText` / `errorType`, leave the session blocked, and complete the inbound. It MUST NOT invoke the user handler.
- FR-015: Existing `SessionBlockedException` and `EventContextHandlerException` catch branches in `StrictMessageHandler` MUST remain unchanged.

#### Manager API

- FR-020: `IManagerClient` MUST gain two methods:
  - `Task CompleteHandoff(MessageEntity pendingEntry, string endpoint, string detailsJson = null)`
  - `Task FailHandoff(MessageEntity pendingEntry, string endpoint, string errorText, string errorType = null)`
- FR-021: Each new method MUST construct a NimBus `Message` of the corresponding new type, populate `From = Constants.ManagerId`, address it to the originating endpoint topic, and send it via the existing `ServiceBusClient`. Same shape as today's `Resubmit` / `Skip`.
- FR-022: `IManagerClient.Resubmit` and `IManagerClient.Skip` MUST remain unchanged in signature and behaviour. They continue to be the operator-initiated recovery primitives.
- FR-023: The Manager MUST validate that `pendingEntry.ResolutionStatus == Pending` (and `PendingSubStatus == "Handoff"`) before issuing `CompleteHandoff` / `FailHandoff`. Calls against any other state return a clear error and emit no message.

#### Resolver & MessageStore

- FR-030: The Resolver `MessageTypeToStatusMap` MUST map `PendingHandoffResponse` → `ResolutionStatus.Pending`. The audit projection MUST be written via the existing `UploadPendingMessage` path — no new container, no new query path.
- FR-031: `MessageEntity` and `UnresolvedEvent` MUST gain four nullable fields:
  - `string PendingSubStatus` — `null` for ordinary Pending; `"Handoff"` for PendingHandoff.
  - `string HandoffReason` — free-text reason supplied by the handler.
  - `string ExternalJobId` — optional external system identifier.
  - `DateTime? ExpectedBy` — optional deadline used by the timeout sweeper.
- FR-032: All four new fields MUST be persisted by every storage provider satisfying the conformance suite (Cosmos DB, SQL Server, in-memory). Existing rows MUST continue to project with these fields as `null`.
- FR-033: `HandoffCompletedRequest` MUST cause the Resolver (via the resulting `ResolutionResponse`) to flip the projection from Pending → Completed. The sub-status MAY be cleared on the projection.
- FR-034: `HandoffFailedRequest` MUST cause the Resolver (via the resulting `ErrorResponse`) to flip the projection from Pending → Failed. The supplied `errorText` MUST be preserved verbatim on the audit row (cf. NFR-004). `errorType` on the audit row reflects the synthetic `HandoffFailedException` wrapper that drives the existing `SendErrorResponse` path, not the operator-supplied value — operators read `errorText`; tightening the `errorType` round-trip is a follow-up. See ADR-012 § Negative.

#### WebApp

- FR-040: WebApp endpoint dashboards MUST count Pending+Handoff entries in the existing Pending column (no new column required). A small visual badge ("Awaiting external") SHOULD render on the message row when `PendingSubStatus = "Handoff"`.
- FR-041: WebApp message detail page MUST render the new sub-status fields (`PendingSubStatus`, `HandoffReason`, `ExternalJobId`, `ExpectedBy`) when present. When absent, the page renders unchanged.
- FR-042: WebApp Resubmit / Skip buttons MUST be available on Pending+Handoff entries, taking the existing `ManagerClient.Resubmit` / `Skip` paths. Operator-initiated `CompleteHandoff` / `FailHandoff` buttons are out of scope for v1.
- FR-043: No WebApp API contract change is required. The new fields are optional additions to existing response shapes.

#### Timeout sweeper (opt-in, deferred — not built in v1)

> **v1 status:** the sweeper design below is in scope for the spec but **deliberately not built in v1** (ADR-012 § Operational). Adapters with bounded reasonable wait times can opt in via `ExpectedBy` once the sweeper ships; the default is "no sweeper running" so adapters with unbounded reasonable wait times stay unaffected today.

- FR-050: NimBus MAY implement a Resolver-side background pass that scans Pending+Handoff entries with non-null `ExpectedBy` in the past, and emits a synthetic `HandoffFailedRequest` with `errorType = "TimeoutExpired"` and a configurable `errorText`.
- FR-051: The sweeper MUST be opt-in. The default state is "no sweeper running". Adapters that want timeout enforcement enable it via configuration.
- FR-052: The sweeper MUST NOT touch Pending entries with `PendingSubStatus = null` (legacy Pending) or with `ExpectedBy = null`.
- FR-053: After a sweep, the resulting Failed entry MUST be reachable through the existing operator Resubmit / Skip flow.

#### Backwards compatibility

- FR-060: Existing handlers, audit rows, transports, and WebApp views MUST keep working unchanged. Adapters that do not call `MarkPendingHandoff` see zero behaviour change.
- FR-061: The new `MessageType` values MUST be additive. Existing forwarding subscriptions that route response messages to the Resolver pick them up with no topology change.
- FR-062: The four new `MessageEntity` fields MUST be nullable in every storage provider. No data migration is required for existing rows.
- FR-063: `IEventHandler<TEvent>.Handle`, `IMessageContext`, `ISender`, and the `AddNimBus*` registration APIs MUST NOT change.

#### Documentation, tests, and ADR

- FR-070: A new ADR MUST be added (suggested: `docs/adr/012-pending-handoff.md`) capturing the rationale and the rejected alternatives (throw-based signalling, repurposing `Resubmit` for the success case).
- FR-071: The shared provider conformance suite (`NimBus.MessageStore.*Tests`) MUST gain test cases verifying that the four new fields round-trip correctly and that the Resolver projection flips Pending → Completed under `HandoffCompletedRequest`.
- FR-072: `tests/NimBus.Core.Tests` MUST cover: handler success path with `MarkPendingHandoff`, handler failure path with `MarkPendingHandoff` (failure wins), middleware observing `ctx.Outcome`, and the two new `Handle*Request` methods (authorize, verify, settle, no handler invocation).
- FR-073: `tests/NimBus.EndToEnd.Tests` MUST gain a scenario covering: handler signals PendingHandoff → siblings defer → external `CompleteHandoff` → Pending → Completed → siblings replay in FIFO order. A second scenario covers `FailHandoff` → Pending → Failed with the error text preserved.
- FR-074: `docs/error-handling.md` and `docs/message-flows.md` MUST be updated to document the new outcome and the two new control messages.
- FR-075: SDK API reference (`docs/sdk-api-reference.md`) MUST document `IEventHandlerContext.MarkPendingHandoff` with a worked example.

### Non-Functional Requirements

- NFR-001: The new `MarkPendingHandoff` method MUST be safe to call from any pipeline phase — handler body, middleware before-await, middleware after-await — without changing observable behaviour beyond the documented one.
- NFR-002: Adding the feature MUST NOT introduce control-flow-by-exception in NimBus's own code paths. The implementation reads `ctx.Outcome` synchronously after the handler returns; no new exception types are added for the happy path.
- NFR-003: Settling a PendingHandoff message MUST NOT require an extra Service Bus delivery beyond the single `HandoffCompletedRequest` / `HandoffFailedRequest`. (No round-trip through `ResubmissionRequest` to obtain a `ResolutionResponse`.)
- NFR-004: The DMF error text passed to `FailHandoff` MUST be preserved verbatim on the resulting audit row, subject to existing column-length limits documented per provider.
- NFR-005: Subscriber processes MUST continue to depend only on Service Bus session state (ADR-002 respected). The feature MUST NOT introduce a database dependency on the subscriber side. Adapter-owned correlation stores live in the adapter, not in NimBus.
- NFR-006: All new public API surface MUST carry XML doc comments. Breaking changes are not introduced; the feature is purely additive.
- NFR-007: The feature MUST be compatible with the repository's `net10.0` target and existing analyzer / packaging conventions.
- NFR-008: WebApp UI changes MUST degrade gracefully on legacy data (rows with `PendingSubStatus = null` render as before).

## Key Entities

- **HandlerOutcome** — enum capturing what the handler signalled (e.g., `Default` = ordinary success, `PendingHandoff`). Internal to `NimBus.SDK.EventHandlers` and `NimBus.Core`.
- **HandoffMetadata** — value object carried on `IEventHandlerContext` after `MarkPendingHandoff`: `reason`, `externalJobId`, `expectedBy`. Surfaced on response messages and on the resulting `MessageEntity`.
- **PendingHandoffResponse** — subscriber-emitted response message. Carries `HandoffMetadata`. Mapped to `ResolutionStatus.Pending`.
- **HandoffCompletedRequest** — Manager-issued control message. Drives Pending → Completed without re-invoking the handler.
- **HandoffFailedRequest** — Manager-issued control message. Drives Pending → Failed and carries `errorText` / `errorType`.
- **PendingSubStatus** — string discriminator on `MessageEntity` / `UnresolvedEvent`. `null` for ordinary Pending, `"Handoff"` for the new outcome.
- **TimeoutExpiredError** — synthetic error type emitted by the optional Resolver sweeper when `ExpectedBy` is exceeded.

## Success Criteria

### Measurable Outcomes

- SC-001: An adapter handler that calls `ctx.MarkPendingHandoff(reason, externalJobId, expectedBy)` and returns produces an audit row with `ResolutionStatus = Pending`, `PendingSubStatus = "Handoff"`, and the supplied metadata, without throwing any exception.
- SC-002: While a session is in PendingHandoff state, subsequent messages on the same session are parked on the Deferred subscription in FIFO order, identical to today's failure-blocked session behaviour.
- SC-003: `ManagerClient.CompleteHandoff(pendingEntry, endpoint)` settles the message as Completed and unblocks the session **without invoking the user handler**. Verified by asserting `IEventHandler<TEvent>.Handle` is not called during the settlement.
- SC-004: `ManagerClient.FailHandoff(pendingEntry, endpoint, errorText, errorType)` settles the message as Failed with `errorText` preserved verbatim on the audit row, and the session remains blocked.
- SC-005: WebApp Pending count includes Pending+Handoff entries; Failed count does not. The message detail page renders `HandoffReason`, `ExternalJobId`, `ExpectedBy` when present and is unchanged on legacy rows.
- SC-006: Existing operator Resubmit / Skip flows work unchanged on Pending+Handoff entries.
- SC-007: The shared provider conformance suite passes with the four new nullable fields round-tripping correctly across Cosmos DB, SQL Server, and in-memory providers.
- SC-008: End-to-end test "PendingHandoff → siblings defer → CompleteHandoff → siblings replay in FIFO" passes against the EndToEnd test harness.
- SC-009: End-to-end test "PendingHandoff → FailHandoff → audit row carries DMF-style error text → operator Skip" passes against the EndToEnd test harness.
- SC-010: No existing test in the repository starts failing as a result of the changes (additive feature).

## Assumptions

- The motivating use case (D365 F&O DMF) is representative — the feature is designed for any external long-running settlement pattern, not just F&O.
- Adapter-side concerns (correlation store, status checker, polling cadence) are owned by the adapter, not NimBus. Subscribers retain their pure Service Bus dependency footprint.
- The shape of `IEventHandler<TEvent>.Handle(TEvent, IEventHandlerContext, CancellationToken)` is stable; we extend `IEventHandlerContext`, not the handler signature.
- The Manager continues to be the authoritative source of control messages; `From = Constants.ManagerId` remains the authorization marker.
- The optional sweeper is genuinely optional. Some integrations have unbounded reasonable wait times; forcing a global timeout would be wrong.

## Out of Scope

- Adapter-side correlation store, status checker, or DMF-specific code.
- Operator-initiated `CompleteHandoff` / `FailHandoff` buttons in the WebApp (Resubmit / Skip remain the operator-facing primitives in v1).
- A general external-completion API that bypasses the Manager (e.g., a direct Resolver write). All settlement flows through Service Bus messages.
- Cross-session ordering. Sessions remain independent.
- Persistence-format migrations. New fields are nullable; existing rows are unaffected.
- Renaming or restructuring `IEventHandler` / `IEventHandlerContext`.
- Telemetry / OpenTelemetry attribute additions beyond what falls out naturally from the new `MessageType` values.

## Open Questions

- **Sweeper hosting.** Resolver-side background pass vs separate worker vs adapter-side. *Design decided (Resolver-side, opt-in via configuration); build deferred — not shipped in v1, see ADR-012 § Operational.*

## Resolved Questions

- The new outcome MUST be signalled by an explicit method on `IEventHandlerContext`, not by throwing. (Resolved during review — control flow by exception is rejected.)
- The audit row MUST be `Pending` (not `Failed`). (Resolved — failure semantics are wrong for healthy in-flight workloads.)
- Settlement MUST use dedicated terminal commands (`CompleteHandoff` / `FailHandoff`), not `Resubmit` / `Skip`. (Resolved — `Resubmit` re-runs the handler, doubles middleware metrics, and mislabels audit attribution.)
- Settlement MUST NOT re-invoke the user handler. (Resolved — `Handle*Request` methods drive the state machine directly, like today's `HandleSkipRequest`.)
- Adapter-side concerns (correlation store, status checker) are out of scope for the framework feature. (Resolved — ADR-002 keeps subscribers pure Service Bus consumers; the adapter owns its own state.)
- The feature is additive only. No breaking changes to existing handlers, transports, audit rows, or APIs. (Resolved.)
- **Naming.** Resolved as `MarkPendingHandoff` (ADR-012 § Decision).
- **Outcome state shape on the context.** Resolved as `HandlerOutcome` enum + `HandoffMetadata` record, both in `NimBus.Core.Messages` (ADR-012 § Decision; the layering note explains why Core, not the SDK, owns the type).
- **Concrete authorization for `HandoffCompletedRequest` / `HandoffFailedRequest`.** Resolved as reuse of `AuthorizeManagerRequest` — `From = Constants.ManagerId` (ADR-012 § Decision; implemented in `StrictMessageHandler.HandleHandoff*Request`).
- **Behaviour when `CompleteHandoff` / `FailHandoff` is called against a non-PendingHandoff state.** Resolved as upfront validation in `ManagerClient` throwing `InvalidOperationException` if `pendingEntry.PendingSubStatus != "Handoff"` (ADR-012 § Decision).
- **WebApp UX.** Resolved — Pending+Handoff rows count under Pending, an "Awaiting external" badge renders on the row, and a Handoff-details panel surfaces the metadata on the message detail page; no dedicated filter chip beyond the badge in v1 (FR-040..FR-042; ADR-012 § Operational).
- **Message detail page surfacing of `HandoffReason`/`ExternalJobId`.** Resolved as a "Handoff details" section on the existing message detail page (FR-041; ADR-012 § Operational).
