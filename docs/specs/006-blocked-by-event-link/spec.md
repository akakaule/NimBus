# Feature Specification: "Blocked By" Event Link on Deferred Message Details

Feature Branch: `006-blocked-by-event-link`
Created: 2026-05-28
Updated: 2026-05-28
Status: Proposed (back-port candidate from DIS; see DIS commit `9071863b`).
Input: User description: "When an operator opens the Event Details page for a deferred event in the WebApp, the page tells them the event is Deferred but not *what* is blocking it. The deferral error text already names the blocking event — 'Session {sessionId} is blocked by {eventId}' — but there is no link, so the operator has to copy the GUID out of the error text and paste it into the URL to navigate to the head of the queue. We want a 'Blocked by' property on the Deferred panel with a click-through link to the blocking event's details page."

## Problem

NimBus's session-based ordering deliberately funnels every message on the same `SessionId` through a single head-of-queue. When the head fails or marks the session blocked (via `BlockSession`, today's failure path; or via `MarkPendingHandoff`, the explicit handoff path per spec 002), every subsequent message on the same session is rejected with a `SessionBlockedException` and parked on the Deferred subscription in FIFO order. The exception's text — formatted by `StrictMessageHandler` as `Session {sessionId} is blocked by {eventId}` — is the only audit-trail link between a deferred sibling and the upstream blocker.

That text *is* persisted on every deferred history entry. The WebApp Event Details page already renders the error text. But:

1. The blocking event id is buried inside a sentence, not surfaced as a structured field.
2. There is no link from the deferred event to the blocking event. Operators triaging a stalled session must copy the GUID out of the error and paste it into the URL bar — every time.
3. On endpoints with deep session backlogs, this turns into a chain of copy-paste-navigate steps. Operators reasonably skip the link entirely and just go look at the queue head by gut feel, which loses information about which exact event opened the block.

The fix is small: parse the GUID out of the existing error text, attach it as a structured field on the deferred-event view model, render it as a clickable link in the property panel. No transport, storage, or API contract change required.

## Scope

In scope:
- A pure-function `parseBlockedByEventId(errorText)` in the WebApp ClientApp that extracts the blocking event GUID from the StrictMessageHandler-formatted error string.
- A `blockedByEventId` prop on the message-listing component, populated from the parser by the Event Details page.
- A new **Blocked by** row in the Properties panel of `event-details/message-listing.tsx`, rendered only when the event's status is `deferred` and a blocking GUID could be parsed. The value is a `Link` to the blocking event's details page within the same endpoint.

Out of scope:
- Changing the format of `SessionBlockedException.Message`. The parser is anchored to the existing text, which is a stable Core string already used in dozens of audit rows.
- Persisting `BlockedByEventId` as a first-class field on the message store. The error text already carries it; promoting it to a typed field is a separate, larger change.
- A new REST endpoint or API contract update.
- A reverse view ("messages I am blocking"). The blocker is canonical (one event blocks the session); listing the blocked siblings from the blocker's perspective is the same information surfaced differently, which can come later if operators ask for it.
- Linking from the deferred event row in the messages-list page. v1 is the Event Details Properties panel only.

## User Scenarios & Testing

### User Story 1 - One click from a deferred event to the head of its session (Priority: P1)

As an operator triaging a deferred event, I want a clickable **Blocked by** link in the Properties panel so I can jump straight to the event that owns the session lock and decide whether to resubmit, skip, or settle a handoff there.

Why this priority: This is the single most common navigation step in deferred-event triage. Today it is a copy-paste chore; closing it is the entire point of the spec.

Independent Test: Trigger a session-blocking event (any failure or PendingHandoff) followed by a sibling on the same session. Open the sibling's Event Details. Click **Blocked by**. Land on the blocking event's Details page in the same endpoint.

Acceptance Scenarios:

1. Given a deferred event whose latest history entry's error text matches `is blocked by {GUID}`, When the Event Details page renders, Then the Properties panel includes a **Blocked by** row whose value is a link to `/Message/Index/{endpointId}/{blockingEventId}/0`.
2. Given the **Blocked by** link is clicked, When the next page renders, Then the user lands on the blocking event's Event Details page within the *same* endpoint as the deferred event.
3. Given the deferred event has multiple history entries (a chain of replays), When the page renders, Then the GUID parsed from the **most recent** deferral error text is used. (Older deferrals may have named a different blocker that has since resolved.)

---

### User Story 2 - The link does not appear when there is no blocker to link to (Priority: P1)

As an operator looking at a non-deferred event, I do not want a misleading **Blocked by** row, because it would suggest a relationship that does not exist.

Why this priority: A row that is sometimes meaningful and sometimes a stale relic confuses more than it helps. Render only when both inputs are present.

Independent Test: Open a completed, failed, skipped, pending, or pending-handoff event. Confirm no **Blocked by** row renders. Open a deferred event whose history text does not match the pattern (legacy row, custom message). Confirm no row renders.

Acceptance Scenarios:

1. Given an event whose `resolutionStatus` is not `Deferred` (case-insensitive), When the page renders, Then no **Blocked by** row is shown.
2. Given a deferred event whose error text does not contain `is blocked by {GUID}` (legacy data, custom handler text), When the page renders, Then no **Blocked by** row is shown and no client-side error is logged.
3. Given a deferred event whose error text contains the phrase but with a malformed GUID (truncated, non-hex), When the page renders, Then `parseBlockedByEventId` returns `undefined` and no row is shown.

---

### User Story 3 - Parser is robust to forward-compatible error text changes (Priority: P2)

As a NimBus maintainer, I want the parser to be tolerant: if a future change wraps or extends the error sentence (e.g., adds a session id callout, prefixes the text with a contextual phrase), the existing link must keep working as long as the canonical `is blocked by {GUID}` substring is intact.

Why this priority: Keeps the parser low-maintenance. The regex anchors only on the meaningful suffix.

Independent Test: Unit-test the parser with the canonical text, a leading-prefix variant, a trailing-suffix variant, and a wrapping-quotes variant. All four should return the GUID.

Acceptance Scenarios:

1. Given the error text exactly matches `Session {sessionId} is blocked by {eventId}`, When the parser runs, Then it returns the GUID.
2. Given a wrapped form such as `Deferral reason: Session N is blocked by {GUID}.`, When the parser runs, Then it still returns the GUID.
3. Given a casing variation (`IS BLOCKED BY`), When the parser runs, Then it returns the GUID. (The regex is case-insensitive on the anchor phrase but case-sensitive on the GUID character class, which is already case-insensitive by hex membership.)

---

### User Story 4 - Visiting the blocker from the deferred page shows operator actions (Priority: P2)

As an operator who has clicked through to the blocker, I want the blocker's Event Details page to render as usual — Resubmit / Skip if failed, the Handoff hero if pending-handoff, etc. — so I can resolve the head of the queue without further navigation.

Why this priority: The link is only useful if the destination is the same Event Details page the operator already knows.

Independent Test: Click **Blocked by** on a deferred event whose blocker is failed. Confirm Resubmit / Skip buttons render. Click **Blocked by** on a deferred event whose blocker is pending-handoff. Confirm the Handoff hero renders.

Acceptance Scenarios:

1. Given the blocker is failed, When the operator clicks **Blocked by**, Then the destination page shows Resubmit / Skip on the standard message header.
2. Given the blocker is pending-handoff, When the operator clicks **Blocked by**, Then the destination page shows the Handoff hero (per spec 002, `HandoffHero`).
3. Given the blocker resolved between the operator opening the deferred page and clicking the link, When the destination page renders, Then it shows the resolved state (Completed / Skipped). No special "stale link" handling is needed — the destination is just whatever the blocker is now.

---

## Edge Cases

- Error text is `null` / `undefined` (legacy row, partial fetch) → parser returns `undefined`; no row rendered.
- Error text matches the phrase but the GUID is missing trailing characters (truncation in storage) → regex does not match the full 36-character pattern; returns `undefined`; no row rendered.
- The deferred event's blocker no longer exists in the endpoint's storage (purged, container rebuilt) → the link still renders pointing at the GUID; the destination page handles the missing event the same way it handles a typo'd URL (its own 404 / "not found" view). This spec does not silently hide the link based on destination existence; doing so would require a per-render lookup.
- The deferred event and the blocker live in different endpoints. *This cannot happen by design* — sessions are scoped to an endpoint subscription. The link uses the current event's `endpointId`, which is also the blocker's endpoint. Guarded by spec 002 / ADR-001.
- The history contains multiple deferral error entries from different replays. The component already iterates the history to compute the latest blocker; the most-recent match wins. Older entries are ignored.
- Two siblings deferred at different times on the same session — they both link to the same blocker. Correct and intended.
- A custom adapter has thrown a `SessionBlockedException` with a non-standard message (no `is blocked by {GUID}` substring). Parser returns `undefined`; no row. The Properties panel still renders the rest of the existing fields unchanged.

## Requirements

### Functional Requirements

#### Parser

- FR-001: A pure function `parseBlockedByEventId(errorText: string | null | undefined): string | undefined` MUST exist in the WebApp ClientApp, exported from a stable module path so it can be unit-tested in isolation. Suggested location: `functions/endpoint.functions.ts` (matches DIS).
- FR-002: The parser MUST accept `null` and `undefined` and return `undefined` without throwing.
- FR-003: The parser MUST match `is blocked by` followed by whitespace followed by a canonical 8-4-4-4-12 hexadecimal GUID. The anchor phrase MUST be matched case-insensitively. The GUID character class is hex (`[0-9a-fA-F]`) so casing of hex digits is naturally tolerated.
- FR-004: The parser MUST return the matched GUID exactly as it appears in the input (no lowercasing, no normalization), so the rendered link's URL matches the route format the destination page expects.
- FR-005: On no match, the parser MUST return `undefined`. It MUST NOT throw or log a console error.

#### View model wiring

- FR-010: `event-details/message-listing.tsx` MUST accept an optional `blockedByEventId?: string` prop.
- FR-011: The Event Details page (`pages/event-details.tsx`) MUST compute `blockedByEventId` by iterating the deferred event's message history (most recent first), running `parseBlockedByEventId` on each entry's error text, and using the first defined result. The chosen entry MUST be the most recent deferral, since older deferrals can name a different blocker that has since resolved.
- FR-012: The computed value MUST be passed into `MessageListing` as the `blockedByEventId` prop. When no entry yields a match, the prop is omitted.

#### Rendering

- FR-020: The Properties panel of `MessageListing` MUST render a **Blocked by** `PropertyRow` if and only if **all three** conditions hold:
  1. `event.resolutionStatus?.toLowerCase() === "deferred"`.
  2. `blockedByEventId` is a non-empty string.
  3. `event.endpointId` is a non-empty string.
  Condition (3) prevents rendering a broken link in the corner case where the event's endpoint id is unexpectedly missing (e.g., a partial fetch or legacy row). The link's URL embeds `endpointId` — without it the destination route does not resolve.
- FR-021: The value of the row MUST be a `react-router-dom` `<Link>` whose `to` is `/Message/Index/{event.endpointId}/{blockedByEventId}/0` and whose visible text is the GUID itself.
- FR-022: The link MUST use the existing primary-text colour and hover style used elsewhere in the panel — no custom button or external-link affordance. The destination is the same site, the same SPA, the same browser tab.
- FR-023: The row MUST use the `mono` variant of `PropertyRow` to match the surrounding GUID fields (`Endpoint`, `Message id`, etc.).
- FR-024: The row MUST be placed inside the same "Properties" `PropertySection` as `From`, `To`, `Endpoint role`, `Message type`, `Originating message`. Suggested ordering: immediately after `Originating message`, since both fields point at "another message in the system."

#### Tests

- FR-030: A unit test in `ClientApp` MUST cover the parser cases enumerated in User Story 3 and the Edge Cases section: canonical match, prefix-wrapped, suffix-wrapped, casing variant on the phrase, undefined input, malformed-GUID input.
- FR-031: A Vitest component test MUST verify the row renders for a deferred fixture with a valid `blockedByEventId` prop and does not render for a deferred fixture without one, and does not render for a non-deferred fixture even if the prop is provided.

#### Documentation

- FR-040: An inline code comment on the row's conditional MUST explain why both conditions are required (one sentence pointing at this spec id).
- FR-041: No user-facing documentation update is required.

### Non-Functional Requirements

- NFR-001: The parser MUST be a single regex pass per entry — no nested loops, no allocations beyond the regex match.
- NFR-002: The component change MUST add zero round-trips to the API. All inputs are already on the page.
- NFR-003: The link MUST use client-side routing (no full page reload), so the click-through is instant.
- NFR-004: The row MUST gracefully degrade when `endpointId` is missing — falling back to omitting the row rather than rendering a broken link. This is enforced structurally by FR-020 condition (3); the NFR exists as the user-impact statement of the same constraint.
- NFR-005: No new ClientApp dependency is added.

## Key Entities

- **`parseBlockedByEventId(errorText)`** — pure function. Returns the blocker's event id when the error text matches the canonical pattern, `undefined` otherwise.
- **`blockedByEventId` prop** — optional string carried into `MessageListing` from the page-level event-details composition. Drives the conditional render of the Properties row.
- **`SessionBlockedException.Message`** — Core-side source of truth for the error text. Format: `Session {sessionId} is blocked by {eventId}`. *Not changed* by this spec.

## Success Criteria

### Measurable Outcomes

- SC-001: On a deferred event whose blocker exists, the Event Details Properties panel shows a **Blocked by** row whose link, when clicked, navigates to the blocker's Event Details page in the same endpoint, without a full-page reload.
- SC-002: On a non-deferred event, no **Blocked by** row is rendered. The Properties panel is otherwise unchanged from today.
- SC-003: On a deferred event whose error text does not match the canonical pattern (legacy or custom message), no row is rendered and no console error is logged.
- SC-004: Unit tests for `parseBlockedByEventId` cover at minimum: canonical match, prefix wrapped, suffix wrapped, casing on phrase, undefined / null input, malformed GUID. All pass.
- SC-005: A Vitest component test confirms render-conditionality across (deferred + prop), (deferred + no prop), (non-deferred + prop). All three behave per FR-020.
- SC-006: No existing WebApp test fails as a result of the change.

## Assumptions

- The canonical error text `Session {sessionId} is blocked by {eventId}` is a stable Core-side format. Verified by inspection of `StrictMessageHandler`'s `SessionBlockedException` throw site.
- The destination route `/Message/Index/{endpointId}/{eventId}/0` is the existing Event Details URL. (The trailing `/0` is the message-history slot index; `0` is the default initial-history-row anchor and matches today's link-from-search behaviour.)
- Sessions are endpoint-scoped: the deferred event and its blocker share an `endpointId`. Holds by design per ADR-001.
- The message history is the only source of the blocker id available to the page. No separate REST call is required; the existing fetch already returns it.

## Out of Scope

- Persisting `BlockedByEventId` as a typed field on `MessageEntity` / `UnresolvedEvent` to remove the parser. Would let the link survive future error-text changes but adds storage migration work for a single field that is reliably present in the text today.
- A reverse "blocking these N events" view on the blocker's page. Useful but a separate spec.
- Linking from the deferred row on the messages-list page or from search results. v1 stays inside the Event Details panel.
- Surfacing the session id in the same panel. A separate "Session" filter / view is tracked elsewhere.
- Telemetry / OpenTelemetry attributes carrying the blocker relationship. The text → link pattern is UI-only; tracing is unchanged.

## Open Questions

- **Mid-screen handoff vs. property row placement.** When the blocker is a PendingHandoff and the deferred event lands beneath the Handoff hero on the destination page, the operator's eye needs to be drawn to "this is the head of the queue, settle it here." Initial design uses the same standard layout; if operator feedback says the handoff hero should call out the count of blocked siblings, that is a follow-up. *Not blocking — kept narrow for v1.*

## Resolved Questions

- The link is rendered **inside the existing Properties panel**, not as a top-level callout on the page. Resolved — keeping with the existing Properties-panel idiom matches every other "navigate to a related message" affordance (e.g., `Originating message`).
- The link target uses the **current event's `endpointId`**, not a separately resolved blocker endpoint. Resolved — sessions are endpoint-scoped (ADR-001) so the two are always equal.
- The parser anchors on the **canonical error text**, not on a new typed field. Resolved — promoting the relationship to a typed field is a bigger change for a marginal robustness gain.
- The most-recent deferral entry wins when the history contains multiple. Resolved — older entries can name an already-resolved blocker.
- The row is rendered only when both `status === deferred` *and* a GUID was parsed. Resolved — either alone is misleading.
