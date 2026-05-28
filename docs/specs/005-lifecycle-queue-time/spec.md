# Feature Specification: Lifecycle Queue Time on Waited (Deferred / Pending-Handoff) Events

Feature Branch: `005-lifecycle-queue-time`
Created: 2026-05-28
Updated: 2026-05-28
Status: Proposed (back-port candidate from DIS; see DIS commits `d49157c` and `17a695e2`).
Input: User description: "Deferred and pending-handoff events in the WebApp currently show a queue time of approximately zero on the Event Details page. The displayed value is derived from the *last* delivery — by then the message has just been re-pulled from the Deferred subscription or the Resolver has just settled the handoff, so the captured `queueTimeMs` reflects the millisecond between dequeue and pickup, not the time the event actually spent waiting. We want the timing bar to describe the full lifecycle — first `EventRequest` through final handler start — for events that experienced a Deferral or PendingHandoff wait."

## Problem

For events with a "linear" lifecycle (one EventRequest → handler → ResolutionResponse), NimBus's row-level `queueTimeMs` is correct: it is captured at receive as `now - EnqueuedTime`, which matches the natural definition of "how long did this event sit in the queue."

For events with a *waited* lifecycle the same field describes only the final segment, because each segment is processed by its own delivery and the row records the most recent one:

- **Deferred** events spend time parked on the `deferred` subscription while another event holds the session lock. When `ContinueWithAnyDeferredMessages` later replays them, the new delivery's `queueTimeMs` is the gap between replay-enqueue and replay-pickup — often a few hundred milliseconds. The 30 seconds (or 30 minutes) the message actually spent waiting do not appear anywhere on the row.
- **Pending-Handoff** events are settled by `HandoffCompletedRequest` after the external job reports back. The audit row's final delivery is the control message; its `queueTimeMs` describes only the control-message round-trip. The actual handoff duration — from the first `PendingHandoffResponse` to the settlement — is missing from the timing bar.

The effect is a timing bar that says "Queue: 12 ms · Processing: 45 ms" on an event whose end-to-end lifecycle took half an hour. Operators triaging slow integrations cannot tell from the page whether the time was spent waiting on a sibling, waiting on an external system, or actually processing — which is the question the page exists to answer.

The message history table on the same page does carry every segment with timestamps; the fix is therefore client-side: derive the "queue" portion from the history when a lifecycle wait is present, and fall back to the existing row-level value when it isn't.

## Scope

In scope:
- A pure-function `getHistoryQueueTimeMs(messages)` in the WebApp ClientApp that returns the wait span only when the history contains at least one `DeferralResponse` or `PendingHandoffResponse`. Returns `undefined` otherwise. The function derives both the start and end anchors from the message history itself — it does NOT depend on any field that is absent from NimBus's current `Event` DTO (see Resolved Questions for why `event.createdAt`, used by DIS, is not consumed).
- Wiring the message history through `pages/event-details.tsx` into `event-details/message-listing.tsx` as an explicit `messages` prop. The page already fetches history via `getEventDetailsHistoryId(params.id, params.endpointId)` and stores it as `histories`, but today passes the value only to `FlowTimeline` (line 219+ of `event-details.tsx`) — this spec extends the prop set on `MessageListing`.
- Wiring the history-derived value into `event-details/message-listing.tsx` so the timing bar prefers it over `queueTimeMs` when present, and otherwise falls back to today's behaviour.
- Updating the timing-bar label (and the existing "Above P95" callout, if applicable) so the segment colours and totals continue to compose correctly.

Out of scope:
- Backend changes. The WebApp REST API already returns the message history needed to compute the span (`GET /api/events/{id}/messages` and equivalent).
- Changing the captured `queueTimeMs` semantics on the row itself or anywhere else (metrics, search, list pages). Only the Event Details timing bar changes.
- A new "Wait" segment in the timing bar in addition to Queue + Processing. The history-derived span is reported under the existing **Queue** label.
- Handling clock-skew edge cases beyond returning `undefined` when the computed span is negative — operators see the row-level value in that case, identical to today.
- Linking the queue segment to the upstream blocking event (covered by Spec 006 `006-blocked-by-event-link`).

## User Scenarios & Testing

### User Story 1 - Deferred event shows real wait, not the replay delay (Priority: P1)

As an operator triaging a slow deferred event, I want the timing bar on Event Details to show the actual queue time the event spent waiting on the session lock — not the millisecond replay delay — so that I can tell at a glance whether the slowness was waiting, processing, or something else.

Why this priority: This is the central case the spec exists to fix. Today the timing bar is silently misleading on the exact events operators look at most.

Independent Test: Trigger an event that defers behind another event for at least one second, then resolves. Open Event Details. Assert the **Queue** segment shows the deferral duration (history first-request → final handler start), not the row-level replay delay.

Acceptance Scenarios:

1. Given an event whose message history contains an `EventRequest` followed by one or more `DeferralResponse` entries and then a final `EventRequest` + `ResolutionResponse`, When the Event Details page renders, Then the **Queue** segment shows `(final handler start) − (first EventRequest)` and the total time reflects the real end-to-end duration.
2. Given an event with no `DeferralResponse` or `PendingHandoffResponse` in its history, When the Event Details page renders, Then the **Queue** segment is unchanged from today: it uses `queueTimeMs` (or the row-derived `enqueuedTimeUtc → createdAt` fallback).
3. Given a deferred event whose history is partial (missing the original `EventRequest` because it has aged out of retention), When the page renders, Then the function uses the earliest available history entry as the start anchor; if the resulting span is non-negative it is shown, otherwise the timing bar silently falls back to `queueTimeMs`.

---

### User Story 2 - Pending-handoff event shows the external-system wait (Priority: P1)

As an operator triaging a long-running handoff, I want the timing bar to show how long the message was waiting on the external system, so that "is this stuck?" and "is this just slow?" become answerable from the same view I already use.

Why this priority: PendingHandoff exists explicitly so the audit row reflects an in-flight external job (spec 002). The timing bar should reflect the same truth.

Independent Test: Drive a handler to call `MarkPendingHandoff`. Wait at least one second before calling `CompleteHandoff`. Assert the **Queue** segment on Event Details covers the handoff wait, not the control-message round-trip.

Acceptance Scenarios:

1. Given an event whose history contains `EventRequest` → `PendingHandoffResponse` → `HandoffCompletedRequest` → final `ResolutionResponse`, When the page renders, Then the **Queue** segment shows `(final handler start) − (first EventRequest)`.
2. Given a still-pending handoff (no settlement message has been received yet), When the page renders, Then the segment uses `(latest PendingHandoffResponse timestamp) − (first EventRequest)` as the wait so far. The bar continues to update as the operator refreshes.
3. Given the event resolves via `HandoffFailedRequest` rather than `HandoffCompletedRequest`, When the page renders, Then the **Queue** segment uses the same start → final-handler-start span.

---

### User Story 3 - Non-waited events keep today's exact behaviour (Priority: P1)

As an operator looking at a normal completed event, I want the timing bar to keep showing the row-level `queueTimeMs` and `processingTimeMs` it shows today, so that this change has no observable impact on the events I look at most often.

Why this priority: The history-derived calc must be opt-in by lifecycle shape. Touching the timing for the 99 % path would be a regression.

Independent Test: Open any completed event whose history is `EventRequest → ResolutionResponse`. Confirm the timing bar segments and totals are identical before/after the change.

Acceptance Scenarios:

1. Given an event whose history contains no `DeferralResponse` and no `PendingHandoffResponse`, When the page renders, Then the function returns `undefined` and the existing `queueTimeMs` value drives the bar.
2. Given an event whose `queueTimeMs` is a measured zero (e.g., immediately picked up), When the page renders, Then the zero is preserved — the fallback chain uses `??`, not `||`, so `0` is not coerced to the next fallback.

---

### User Story 4 - The total continues to compose correctly (Priority: P2)

As an operator, I want the **Total** displayed under the timing bar to equal Queue + Processing, so that the numbers add up.

Why this priority: Catches an easy mistake where the override changes Queue but the Total derivation keeps using the row value.

Independent Test: Inspect any deferred or pending-handoff event for which the override fires. The displayed Total must equal the displayed Queue + Processing.

Acceptance Scenarios:

1. Given a deferred event using the history-derived Queue, When the page renders, Then `Total = Queue + Processing` where both segments are the values displayed in the bar.
2. Given a partial history that produces an `undefined` Queue, When the page renders, Then both Queue and Total fall back to today's logic, identical to the non-waited path.

---

## Edge Cases

- History is missing entirely (network error, server returned `[]`) — function returns `undefined`; fallback chain selects `queueTimeMs`, then the row-derived `enqueuedTimeUtc → createdAt` value.
- History contains only a single `EventRequest` (the event has not yet been processed) — function returns `undefined` (no end anchor). Pending events with no processing yet show no Queue segment, identical to today.
- History contains a `DeferralResponse` for which no later `EventRequest` exists yet (still deferred) — the latest end anchor (the deferral itself) is used; span describes "wait so far." A new history entry on refresh extends the bar.
- Clock skew between the SB-recorded `EnqueuedTimeUtc` and the Resolver-recorded message timestamps makes the computed span negative — the function returns `undefined` so the bar silently falls back to the row value.
- The final delivery's `event.createdAt` is missing — function uses the latest end-anchor message timestamp instead. Documented in the inline comment.
- An event with both a `DeferralResponse` *and* a `PendingHandoffResponse` (deferred sibling on a session that was itself blocked by a handoff) — the function treats the whole window as a single lifecycle wait. The Queue segment covers `first EventRequest → final handler start`, which is the correct end-to-end answer.
- A repeat-resubmitted event (`EventRequest` → `ResolutionResponse` → operator clicks Resubmit → new `EventRequest`) without any deferral or pending-handoff — function returns `undefined`. Resubmits without a wait segment continue to show the latest row's `queueTimeMs`. (Repeat-resubmit lifecycle is out of scope and tracked separately.)
- The history contains response-only messages with no request anchor (rare, e.g., legacy rows backfilled from logs) — function returns `undefined`.
- An event with `messageType` values that do not normalize to the known set (forward-compat) — the function ignores them and falls back to `undefined` when no recognised end anchor remains.

## Requirements

### Functional Requirements

#### Client-side calculation

- FR-001: A pure function `getHistoryQueueTimeMs(messages)` MUST exist in the WebApp ClientApp, exported from a stable module path so it can be unit-tested in isolation. Suggested location: `components/event-details/message-listing.tsx` or a sibling module reachable from it. Signature takes only `messages` (no `finalHandlerStartedAt`): NimBus's `Event` DTO does not currently expose a `createdAt` / handler-start field (verified at `Controllers/ApiContract.g.cs:4495` — the partial declares `_updatedAt`, `_enqueuedTimeUtc`, `_queueTimeMs`, `_processingTimeMs`, but no `_createdAt`), and adding one is out of scope here.
- FR-002: The function MUST return `undefined` if the input `messages` array is `null`, `undefined`, or empty.
- FR-003: The function MUST normalize `messageType` to a lowercase, type-free string (e.g., `"eventrequest"`, `"deferralresponse"`, `"pendinghandoffresponse"`) before comparison. Casing differences between transports MUST NOT change the result.
- FR-004: The function MUST return `undefined` if the history contains no `deferralresponse` **and** no `pendinghandoffresponse` entry. The history-derived override applies only to waited events.
- FR-005: When the history is a waited lifecycle, the function MUST identify:
  - A **start anchor**: the earliest `eventrequest` message in the history.
  - An **end anchor**: the latest message whose type is one of `resolutionresponse`, `skipresponse`, `errorresponse`, `unsupportedresponse`, `deferralresponse`, `pendinghandoffresponse`. The DeferralResponse / PendingHandoffResponse anchors are valid as end anchors because still-waiting events have no final outcome yet; the segment shows "wait so far."
- FR-006: When the history is partial (no `eventrequest` available), the function MUST fall back to the earliest available message as the start anchor, and rely on FR-008 to discard the result if the resulting span is negative.
- FR-007: The end-anchor timestamp is the timestamp of the latest end-anchor message in the sorted history. (DIS's optional `finalHandlerStartedAt` parameter — which it uses to override the end anchor with `event.createdAt` of the final delivery — is intentionally omitted in NimBus because the equivalent field is not on the contract.)
- FR-008: The function MUST return `undefined` when the resulting span is negative. This guards clock-skew between the Service-Bus-recorded enqueue time and the Resolver-recorded message timestamps.
- FR-009: The returned value MUST be in milliseconds and consistent with the existing `queueTimeMs` units.

#### History prop wiring

- FR-015: `MessageListing` (in `components/event-details/message-listing.tsx`) MUST accept a new optional `messages?: api.Message[]` prop. The prop is the same array shape `FlowTimeline` already consumes.
- FR-016: `pages/event-details.tsx` MUST pass its existing `histories` state value to `MessageListing` as the `messages` prop. Today (line 219+) `histories` is passed only to `FlowTimeline`; this spec extends the prop set on `MessageListing`. No additional fetch is required — the fetch at line 92-96 already populates `histories`.
- FR-017: When the `messages` prop is omitted or empty, the timing bar MUST fall back to today's behaviour (no override applies). This guarantees the override is gated by data availability, not by data shape.

#### Wiring into the timing bar

- FR-020: `message-listing.tsx` MUST select the Queue value via the chain `getHistoryQueueTimeMs(props.messages) ?? event.queueTimeMs ?? diffMs(event.updatedAt, event.enqueuedTimeUtc)`. The chain MUST use `??`, not `||`, so a genuine measured `0` is preserved. (The third fallback uses `event.updatedAt` because NimBus's `Event` DTO has no `createdAt` field — see FR-001. `updatedAt` minus `enqueuedTimeUtc` is the closest available stand-in for the existing pre-spec fallback and matches today's behaviour for events with no captured `queueTimeMs`.)
- FR-021: Processing time MUST continue to use the existing NimBus chain unchanged. This spec does not modify processing. If the existing chain currently references `event.createdAt`, that reference is independent of this spec; it stays as-is or is fixed separately.
- FR-022: The **Total** beneath the timing bar MUST equal the Queue + Processing values actually displayed in the segments. If Queue is overridden, Total uses the overridden value.
- FR-023: The "Above P95" / `SLOW_PROCESSING_MS` callout MUST continue to evaluate against the Processing segment only — never the overridden Queue value.

#### Backwards compatibility

- FR-030: Events whose history contains no `DeferralResponse` and no `PendingHandoffResponse` MUST render an identical timing bar to today. No row-level field changes, no segment additions.
- FR-031: No REST API change is required. The function consumes the existing `Message[]` shape already delivered by the messages endpoint.
- FR-032: The behaviour MUST be additive on the client. No server-side migration, no message-store change, no metrics-impact.

#### Tests

- FR-040: A unit test in `ClientApp` MUST cover, at minimum, the following cases of `getHistoryQueueTimeMs`:
  1. Empty / undefined messages → `undefined`.
  2. Lifecycle without any deferral or pending-handoff → `undefined` (fallback path is exercised).
  3. Single deferral round-trip → expected span computed from first EventRequest to latest end-anchor message.
  4. Pending-handoff that resolves via `HandoffCompletedRequest` → expected span.
  5. Still-pending handoff (no settlement message yet) → span from EventRequest to latest `PendingHandoffResponse`.
  6. Partial history (no EventRequest) with a valid end anchor → uses earliest available message as start; non-negative result returned.
  7. Clock-skewed history (end anchor before start) → `undefined`.
- FR-041: An integration / Vitest test on `message-listing.tsx` MUST verify that the timing bar's Queue segment displays the overridden value for a deferred event fixture (with `messages` prop populated) and the row value for a non-deferred fixture (also with `messages` prop populated, but without any deferral / pending-handoff entries).
- FR-042: A Vitest test on `pages/event-details.tsx` MUST verify that the `histories` state value flows into the `messages` prop on `MessageListing`. (Guards against the wiring regression: history exists in state but is never passed down.)

#### Documentation

- FR-050: An inline code comment at the call site MUST briefly explain *why* the override exists (one sentence pointing at this spec id), so future maintainers do not regress by replacing the chain with a single `event.queueTimeMs` read.
- FR-051: No user-facing documentation update is required — the timing bar legend ("Queue", "Processing") and units do not change.

### Non-Functional Requirements

- NFR-001: The calc MUST be O(n) over message-history length, with no nested scans. History tables on this page are bounded (the storage layer caps return size); the function MUST not become a performance issue.
- NFR-002: The function MUST be pure: no calls to `Date.now()`, no DOM access, no React-state reads. This keeps it unit-testable and SSR-safe should the WebApp ever pre-render.
- NFR-003: The function MUST tolerate forward-compatible additions to the `messageType` enum without throwing. Unknown types are ignored, not asserted on.
- NFR-004: No new dependency is added to `ClientApp/package.json`. Moment.js (already present) is used for parsing; no additional date library.
- NFR-005: The change MUST NOT alter the rendered behaviour on events that today produce a correct timing bar — only the overridden subset changes.

## Key Entities

- **`getHistoryQueueTimeMs(messages) → number | undefined`** — pure function. Returns a millisecond span for waited lifecycles, `undefined` for everything else.
- **`messages` prop on `MessageListing`** — new optional `api.Message[]` prop. Sourced from `pages/event-details.tsx`'s existing `histories` state.
- **Lifecycle wait** — a message-history shape containing at least one `DeferralResponse` or `PendingHandoffResponse`. The shape that triggers the override.
- **Start anchor** — earliest `EventRequest` in the history (or earliest message when partial).
- **End anchor** — latest message whose type is a recognised lifecycle outcome (resolution / skip / error / unsupported / deferral / pending-handoff response). Its timestamp is the end of the wait span.

## Success Criteria

### Measurable Outcomes

- SC-001: For a deferred event with a one-second-or-greater deferral wait, the Event Details timing bar's **Queue** segment displays the deferral duration to within 1 % of wall-clock truth — not the replay delay.
- SC-002: For a `PendingHandoff → CompleteHandoff` flow with a one-second-or-greater external wait, the timing bar's **Queue** segment displays the handoff duration to within 1 % of wall-clock truth.
- SC-003: For an event whose history is a simple `EventRequest → ResolutionResponse`, the timing bar before and after the change is pixel-identical (same Queue, Processing, Total values, same segment widths).
- SC-004: The unit-test suite (FR-040) passes for all seven enumerated cases.
- SC-005: The Vitest scenario (FR-041) shows the overridden Queue value for the deferred fixture and the row Queue value for the non-deferred fixture.
- SC-006: No existing WebApp test fails as a result of the change.

## Assumptions

- The messages endpoint already returns enough history to compute the span for a waited event (verified in DIS — the same endpoint shape exists in NimBus).
- The Service-Bus-recorded `EnqueuedTimeUtc` and the Resolver-recorded message timestamps are close enough in wall-clock terms (within seconds) for the negative-span guard to be a corner case, not a routine path.
- The Event Details page is the only surface that needs this override today. Endpoint dashboards, the messages list, and search continue to show `queueTimeMs` as-is — those are aggregate views where the row-level number is the right summary.
- The override is a UI choice. The underlying captured `queueTimeMs` is not "wrong" — it is the right value for "how long was this *delivery* queued for." The Event Details page wants a different question answered.

## Out of Scope

- A "Wait" segment alongside Queue and Processing. Bar layout and labels are unchanged.
- Server-side recomputation, persistence, or back-fill of a lifecycle-aware queue metric.
- Metrics dashboards, alert rules, search-result columns. All keep using `queueTimeMs` as-is.
- Linking the overridden Queue segment to the blocking event (separate spec, `006-blocked-by-event-link`).
- Surfacing the contributing segments (deferral wait vs. pending-handoff wait) when both are present in the same lifecycle. v1 reports a single combined span.
- A general-purpose history-traversal utility on the server. The fix is intentionally client-side, kept narrow.

## Open Questions

- None. Behaviour, fallback chain, and edge cases are decided.

## Resolved Questions

- The override fires only on waited lifecycles (deferral or pending-handoff), not on all events. Resolved — applying it everywhere risks regressing the 99 % path where the row value is already correct.
- The override is reported under the existing **Queue** label, not a new "Wait" segment. Resolved — minimal UI churn and the bar's two-segment layout stays intact.
- Negative spans return `undefined` rather than clamp to zero. Resolved — silently fall back to the row value rather than display a nonsensical 0 ms for a half-hour-wait event.
- The fallback chain uses `??`, not `||`, to preserve genuine measured zeros. Resolved — matches the existing code style at the call site.
- The `finalHandlerStartedAt` parameter is intentionally omitted, even though DIS exposes it. Resolved — NimBus's `Event` DTO has no `createdAt` (only `updatedAt`, `enqueuedTimeUtc`, `queueTimeMs`, `processingTimeMs` — verified at `Controllers/ApiContract.g.cs:4495`), and adding a contract field is out of scope. End anchors come from the history.
- The third fallback in the chain uses `event.updatedAt`, not `event.createdAt`. Resolved — `event.createdAt` does not exist on the NimBus contract; `updatedAt` is the closest available stand-in and matches today's pre-spec behaviour for events without a captured `queueTimeMs`.
- `MessageListing` gains an explicit `messages` prop, fed by `pages/event-details.tsx`'s already-fetched `histories` state. Resolved — fetching history twice would be wasteful; the page already has the data.
