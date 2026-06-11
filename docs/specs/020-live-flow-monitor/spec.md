# Feature Specification: Live Flow Monitor — Real-Time Data-Flow Visualization in the WebApp

Feature Branch: `020-live-flow-monitor`
Created: 2026-06-11
Updated: 2026-06-11
Status: Proposed
Input: User description: "The site/demo/index.html would be a cool feature to have in NimBus, another real time monitoring tool (no replacing existing page). Make a solution design for having this capability in the NimBus web app."

## Problem

NimBus ships a marketing demo (`site/demo/index.html`, published on GitHub Pages) that visualizes the platform as an animated SVG diagram: business systems publish events, message dots travel along bezier edges through Service Bus topics into endpoints, outcomes flow to the Resolver, failures park in a deferred strip, and a sidebar shows live counters and an activity log. It is compelling — and entirely fake: 666 lines of self-contained vanilla JS with a hardcoded topology, a virtual-clock animation loop, and randomly generated traffic.

The management WebApp has the real ingredients but no equivalent view:

- The **Topology** page (`ClientApp/src/components/topology/`) renders the real platform shape (producers/consumers per event type from `/api/eventtypes`, traffic-weighted ribbons from `/api/metrics/overview`) but is static — a snapshot, not a live picture.
- The **Monitor** page polls `/api/endpointstatus/all` every 5 s for wall displays — live-ish numbers, no spatial sense of *where* messages flow.
- The **GridEventsHub** SignalR hub (authorized per spec 010) already broadcasts `endpointupdate` events carrying `EndpointStatusCount` (`EndpointId`, `EventTime`, `FailedCount`, `DeferredCount`, `PendingCount`, `UnsupportedCount`, `DeadletterCount`, `SubscriptionStatus`, `StorageStatus`) whenever an endpoint's projected counts change — from the Cosmos change-feed webhook or the Resolver write-path notifier (`IMessageStateChangeNotifier`).

Operators watching an incident currently triangulate between Endpoints, Monitor, and Metrics. A live flow view answers at a glance: *which routes are moving, which endpoint is accumulating failures, where deferred messages are parking* — the demo's exact value, on real data.

This spec designs that capability as a **new page** alongside the existing ones (explicitly: no existing page is replaced; the GitHub Pages demo stays untouched as marketing collateral).

## Proposed Solution

### Architecture overview

```
                       Phase 1 (existing signals only)
┌─────────────┐   GET /api/eventtypes, /api/eventtypes/{id},
│  Flow page   │◄── /api/metadatashort, /api/metrics/overview     (topology shape + seed counters)
│  (/flow)     │
│              │◄── SignalR endpointupdate (EndpointStatusCount)  (live deltas)
│  ┌─────────┐ │
│  │ layout   │ │   Phase 2 (server enrichment)
│  │ engine   │ │◄── SignalR flowactivity ({endpointId, eventTypeId,
│  ├─────────┤ │       status, eventId, occurredAtUtc})
│  │ delta →  │ │        ▲
│  │ events   │ │        │ broadcast by WebApp
│  ├─────────┤ │   POST /api/storagehook/flow  (X-Webhook-Key)
│  │ animator │ │        ▲
│  └─────────┘ │        │ Resolver write path (it just stored the outcome,
└─────────────┘          │ so it knows endpoint, event type, and status)
```

Three client-side modules, one optional server-side enrichment:

1. **Layout engine** — builds the diagram from the real catalog: a layered, automatically positioned graph (no hand-placed coordinates). Columns: *producing endpoints grouped by system* → *endpoint topics* → *consuming endpoints* → *platform* (Resolver, Manager, storage provider). Reuses the bipartite convention already proven in `topology-flow.tsx` (an endpoint that both produces and consumes appears in both columns).
2. **Delta-to-events derivation** — turns the real-time signals into renderable `FlowEvent`s. Phase 1 diffs consecutive `EndpointStatusCount` per endpoint; Phase 2 consumes per-outcome `flowactivity` directly.
3. **Animator** — ports the demo's proven technique (rAF loop, `getPointAtLength` dot positioning, virtual clock with pause/speed) into a React-compatible imperative module. React declaratively renders the static SVG (nodes, chips, edges); a `FlowAnimator` class held in a `useRef` owns the transient dot layer via a single `<g ref>`, so per-frame updates never touch React reconciliation.

### Phase 1 — ship on existing signals (no server changes)

The only live signal today is aggregate: `endpointupdate` says "endpoint X's counts are now {…}". The derivation rules:

| Observed delta (vs. previous snapshot) | Rendered as |
|---|---|
| `PendingCount` +N | N inbound dots, topic → endpoint (event-type color if the route has a single event type, neutral otherwise) |
| `PendingCount` −N with `FailedCount`/`DeferredCount`/`DeadletterCount` unchanged | N completed dots endpoint → Resolver topic, green; `completed` counter +N |
| `FailedCount` +N | N red dots endpoint → Resolver topic; `failed` counter +N; endpoint node enters `attention` state |
| `DeferredCount` +N | N amber dots into the endpoint's parking strip; strip size = current `DeferredCount` |
| `DeferredCount` −N | parked dots release back into the endpoint (resubmit/skip happened) |
| `DeadletterCount` +N | purple dot endpoint → DLQ glyph; `deadlettered` counter +N |
| `StorageStatus` ≠ "ok" / `SubscriptionStatus` degraded | node border state, no dots |

Honest limitation, stated in the UI: Phase 1 animation is *derived from count deltas*, so individual dots are representative, not 1:1 messages, and event-type attribution is inferred from the route. This is the same fidelity the Monitor page offers, presented spatially.

Counters seed from `/api/metrics/overview` for the selected period and advance with deltas. The activity log renders humanized delta lines ("BillingEndpoint: 3 failed (12:04:11)"). The deferred parking strip is fully real even in Phase 1 — `DeferredCount` is authoritative per endpoint.

Resilience: if the hub connection drops, the hook falls back to polling `/api/endpointstatus/all` on the Monitor page's 5 s cadence and keeps diffing — same derivation path, lower latency guarantee, automatic re-upgrade when SignalR reconnects.

### Phase 2 — per-outcome enrichment (server change, additive)

The Resolver write path is the universal interception point: it stores every message outcome regardless of storage provider, and `IMessageStateChangeNotifier.NotifyEndpointStateChangedAsync(endpointId)` already fires there. Phase 2 widens that seam additively:

- New notification carrying outcome metadata (no payloads): `FlowActivity { EndpointId, EventTypeId, Status, EventId, SessionId?, OccurredAtUtc }`.
- Transport Resolver → WebApp: a new storage-hook route `POST /api/storagehook/flow` on the existing anonymous-but-key-gated webhook controller (reusing the `X-Webhook-Key` mechanism hardened in this release). Batched (the notifier buffers ~250 ms and posts arrays) to keep chatter bounded.
- The WebApp broadcasts a new SignalR event `flowactivity` on `GridEventsHub` (additive; `endpointupdate` unchanged, all existing consumers unaffected).
- The Flow page prefers `flowactivity` when present; the Phase 1 delta path remains as fallback so the page works against older Resolvers.

This gives true per-message dots with correct event-type colors and enables the demo's session-blocking story (a `SessionId` on a failed outcome marks the session's subsequent deferred arrivals as "parked behind ORD-1234").

### Phase 3 (optional, separate spec) — blocked-session drill-down

Clicking a parked strip opens the real session view (the admin session preview API exists). Remediation actions (resubmit/skip) from within the flow view. Out of scope here.

### What is deliberately NOT inherited from the demo

- Hardcoded layout, fictional "AI agent" nodes, simulated traffic generator, `min-width:1280px` hard floor (the page gets horizontal scroll + filtering instead).
- The demo file itself is not imported or iframed — the WebApp implementation is a fresh React/TypeScript port of the *technique* (layered SVG + dot animation), themed with the app's Tailwind tokens and dark mode.

## Scope

In scope:

- New lazy-loaded page `Flow` at `/flow`, nav entry in the "Observe" sidebar group (alongside Topology and Monitor).
- Automatic layered layout from `/api/eventtypes`, `/api/eventtypes/{id}`, `/api/metadatashort`.
- SignalR `endpointupdate` subscription with reconnect + polling fallback; delta derivation per the table above.
- Animator module (virtual clock, pause, speed 0.25–4×, dot pooling, `prefers-reduced-motion` and hidden-tab suspension).
- Counters, activity log, deferred parking strips, endpoint attention states.
- Endpoint/event-type filter; default view = top 12 busiest endpoints by the selected metrics period, "show all" toggle.
- Phase 2 server enrichment as specified (new webhook route, `flowactivity` hub event, Resolver notifier extension) — may ship in a follow-up PR but is designed here so Phase 1 makes no decision that blocks it.
- Vitest coverage for layout + delta derivation; MSTest coverage for the Phase 2 webhook → hub path.

Out of scope:

- Replacing or modifying the Topology, Monitor, or Metrics pages.
- Modifying or removing `site/demo/index.html`.
- Message payload display anywhere in the flow view (metadata only — consistent with hub payload policy).
- Per-user endpoint filtering of hub broadcasts (spec 010 already records this as a known limitation; the flow page inherits it).
- Remediation actions from the flow view (Phase 3).
- Mobile layout below ~1024 px (matches Monitor's wall-display posture); the page remains usable with horizontal scroll.

## User Scenarios & Testing

### User Story 1 — Watch live traffic spatially (Priority: P1)

As an operator, I want a live diagram of my actual platform showing messages moving from producers through topics into endpoints, so I can see at a glance which routes are active and healthy.

Why this priority: this is the feature; everything else decorates it.

Independent Test: open `/flow` on a platform with traffic (or use `/api/dev/seed` + manual `endpointupdate` triggers in Development); verify nodes match the real catalog and dots appear within 2 s of an `endpointupdate` broadcast.

Acceptance Scenarios:

1. Given an authenticated user and a platform with 3 endpoints, When they open `/flow`, Then the diagram shows exactly the catalog's endpoints, their topics, and the platform nodes, positioned by the layout engine without overlap.
2. Given the page is open, When an `endpointupdate` arrives with `FailedCount` +2 for BillingEndpoint, Then two red dots animate from BillingEndpoint toward the Resolver topic, the failed counter increments by 2, and the activity log gains one line.
3. Given the SignalR connection drops, When 5 s elapse, Then the page continues updating via polling and shows a "degraded — polling" indicator; when the hub reconnects, the indicator clears.

### User Story 2 — Spot accumulating problems (Priority: P1)

As an on-call engineer, I want failing and deferred messages to be visually loud (red dots, amber parking strips, endpoint attention state), so the flow view works as an incident radar, not just a pretty animation.

Acceptance Scenarios:

1. Given BillingEndpoint has `DeferredCount` 14, When the page loads, Then BillingEndpoint shows a parking strip sized/labeled 14 without any animation needing to occur first.
2. Given an endpoint's `StorageStatus` is "unavailable", When rendered, Then the node shows a distinct degraded border state and a tooltip naming the condition.

### User Story 3 — Control the animation (Priority: P2)

As a user presenting or debugging, I want pause, speed control, and filtering, so the view stays legible under load and in demos.

Acceptance Scenarios:

1. Given heavy traffic, When the user pauses, Then dots freeze, counters stop advancing visually, and buffered deltas apply on resume without being lost.
2. Given a platform with 40 endpoints, When the page loads, Then only the top 12 by traffic render by default, with a filter to add/remove endpoints and a "show all" toggle.
3. Given the OS reports `prefers-reduced-motion`, When the page loads, Then dots are replaced by edge-pulse highlights (no traveling particles) while counters and log behave identically.

### User Story 4 — Per-message fidelity (Phase 2) (Priority: P3)

As an operator on a platform with the enriched Resolver, I want dots colored by actual event type and parked sessions labeled by real session IDs.

Acceptance Scenario: Given the Resolver posts `FlowActivity` batches, When a `CustomerRegistered` outcome with status `failed` arrives, Then a dot in that event type's color travels the correct route and the log line names the event type and event ID (linking to the existing event-details page).

## Edge Cases

- **Burst deltas** (e.g., bulk resubmit changes counts by 500): render at most `MAX_DOTS_PER_DELTA` (default 8) representative dots with a "×500" badge; the log aggregates to one line.
- **Concurrent dot ceiling**: hard cap (default 60 in-flight dots); beyond it, new events update counters/log only and edges pulse instead. No unbounded DOM growth.
- **Counter drift** (Phase 1 derives from deltas; missed broadcasts drift): reconcile counters against `/api/endpointstatus/all` every 60 s; corrections apply silently.
- **First snapshot**: the first `endpointupdate` per endpoint establishes a baseline — never animated (no diff exists), only recorded.
- **Endpoint in update but not in catalog** (provisioned after page load): render a placeholder node and refetch the catalog once, debounced.
- **Hidden tab**: rAF suspends, deltas buffer (bounded ring, latest-wins per endpoint); on visibility regained, counters jump-correct and animation resumes — no catch-up storm.
- **Both-role endpoints**: appear in producer and consumer columns (existing topology-flow convention); dots route to the correct instance per direction.
- **Phase 2 webhook unconfigured key**: identical fail-closed semantics to the existing storage-hook route (shared `ValidateWebhookKey`).

## Requirements

### Functional Requirements

- **FR-001**: A new page at route `/flow`, lazy-loaded, with a sidebar entry "Flow" in the Observe group. No existing route, page, or nav entry changes.
- **FR-002**: The layout engine MUST build nodes/edges exclusively from catalog APIs (no hardcoded topology) and MUST produce a deterministic layered layout: systems/producers → topics → consumers → platform.
- **FR-003**: The page MUST subscribe to `endpointupdate` on the existing `GridEventsHub` connection (reusing the SPA's connection management, including auth cookie and reconnect policy) and MUST degrade to 5 s polling of `/api/endpointstatus/all` when the hub is unavailable, with a visible degraded indicator.
- **FR-004**: Delta derivation MUST follow the mapping table in "Phase 1" above and MUST treat the first snapshot per endpoint as baseline-only.
- **FR-005**: Counters (published/completed/failed/deferred/resubmitted/skipped/deadlettered) MUST seed from `/api/metrics/overview` and reconcile against `/api/endpointstatus/all` at most every 60 s.
- **FR-006**: Deferred parking strips MUST reflect the authoritative `DeferredCount` per endpoint at all times (not only animated deltas).
- **FR-007**: Controls MUST include pause/resume, speed (0.25×–4×), endpoint/event-type filter, and "show all"; defaults: running, 1×, top 12 endpoints by traffic.
- **FR-008**: Animation MUST respect `prefers-reduced-motion` (edge pulses instead of dots) and MUST suspend the rAF loop when `document.visibilityState === "hidden"`.
- **FR-009**: In-flight dots MUST be capped (default 60) and per-delta dots capped (default 8) with multiplier badges; caps are constants, not user settings, in v1.
- **FR-010** (Phase 2): A new route `POST /api/storagehook/flow` on `StorageHookApiController` MUST accept batched `FlowActivity[]`, gated by the existing `X-Webhook-Key` validation, and broadcast each batch as a `flowactivity` hub event. The payload MUST contain metadata only — no message bodies.
- **FR-011** (Phase 2): The Resolver MUST expose an additive notifier abstraction (extending or sibling to `IMessageStateChangeNotifier`) that buffers outcomes ~250 ms and posts batches to the configured WebApp hook URL; default implementation remains no-op, so existing deployments are unaffected.
- **FR-012** (Phase 2): The Flow page MUST prefer `flowactivity` when received and silently fall back to FR-004 derivation otherwise; both paths share the same renderer.

### Non-Functional Requirements

- **NFR-001**: No new runtime frontend dependencies — the animator and layout are vanilla TypeScript + SVG, consistent with `topology-flow.tsx` (no d3 force layout, no animation library).
- **NFR-002**: 60 fps target with ≤60 in-flight dots on a mid-range laptop; per-frame work touches only the imperative dot layer (zero React re-renders per frame; counter/log state updates throttled to ≥250 ms).
- **NFR-003**: Page CPU near-zero when the tab is hidden.
- **NFR-004**: Phase 1 makes zero server-side changes; Phase 2 changes are additive (new route, new hub event name in `Constants.EventSignalNames`, no changes to `endpointupdate` shape or consumers).
- **NFR-005**: The page renders a 50-endpoint catalog in under 2 s on first load (catalog fetches already parallelized per event type in `use-topology-data.ts`; reuse that pattern, share its cache where practical).

## Key Entities

- **FlowNode**: `{ id, kind: "system" | "producer" | "topic" | "consumer" | "platform", title, column, row, health }` — computed by the layout engine.
- **FlowRoute**: `{ id, fromNodeId, toNodeId, eventTypeIds, pathD, length }` — static per layout; `length` memoized from the rendered path for `getPointAtLength`.
- **FlowEvent**: `{ routeId, status, color, multiplier, occurredAt }` — the renderer's unit of work, produced by either derivation path.
- **EndpointStatusDelta**: `{ endpointId, pending, failed, deferred, deadlettered, …, baseline: bool }` — Phase 1 diff result.
- **FlowActivity** (Phase 2 wire type): `{ endpointId, eventTypeId, status, eventId, sessionId?, occurredAtUtc }`.

## Success Criteria

- **SC-001**: An operator can identify the endpoint accumulating failures within 5 seconds of opening `/flow` during a fault injection (red dots + attention state + counter).
- **SC-002**: Dots corresponding to an `endpointupdate` appear within 2 s of the broadcast on a connected client.
- **SC-003**: Existing pages, hub consumers, and the GitHub Pages demo are byte-for-byte unaffected by Phase 1.
- **SC-004**: The delta-derivation module reaches ≥90% branch coverage in vitest (it is pure: snapshot pairs in, `FlowEvent[]` out).
- **SC-005**: With 40 endpoints and sustained updates, the page holds 60 fps and stays under the dot cap (verified with the dev seed + a scripted update burst).

## Assumptions

- The SPA already maintains (or can be refactored to share) a single `GridEventsHub` connection; the Flow page must not open a second connection.
- `EndpointStatusCount` broadcasts fire for all storage providers in deployments that want live updates (Cosmos via change-feed webhook; SQL via the Resolver write-path notifier) — the flow page is only as live as that wiring, and the polling fallback covers the rest.
- The catalog APIs expose enough shape (producers/consumers per event type, endpoint→system mapping via metadata) to attribute routes; where a route hosts multiple event types, Phase 1 colors dots neutrally.
- Built-in platform endpoints (Resolver, Manager) are identifiable in the catalog (by well-known IDs) for placement in the platform column.

## Out of Scope (explicit)

- Replacing Topology/Monitor/Metrics; modifying the demo; payload display; per-user broadcast filtering; remediation actions; mobile-first layout; user-configurable caps; historical replay ("time travel") of past traffic.

## Open Questions

- **OQ-1**: Phase 2 transport — the spec proposes the storage-hook webhook (reuses existing key infrastructure); an Azure SignalR backplane shared by Resolver and WebApp is the alternative if a deployment already runs Azure SignalR. Decide at Phase 2 implementation time; FR-011's abstraction keeps both open.
- **OQ-2**: Should the top-N default persist per user (localStorage, like existing hidden-column persistence) or reset per session? Default proposal: persist filter + speed in localStorage.
- **OQ-3**: Whether `flowactivity` should also flow into the activity log on the existing Monitor page ticker. Cheap if yes, but scope says no page changes — revisit in Phase 2.

## Resolved Questions

- **RQ-1**: *Port the demo file or rewrite?* Rewrite in React/TS. The demo's imperative globals (closure-held `dots`, `timers`, direct DOM `prepend`) fight React reconciliation; only the technique (layered SVG, `getPointAtLength` dots, virtual clock) carries over. The hybrid React-static/imperative-overlay split confines non-declarative code to one ref-owned `<g>`.
- **RQ-2**: *Real per-message animation in v1?* No. The only live signal is aggregate counts; pretending otherwise would be fabrication. Phase 1 is honest about derived dots; Phase 2 buys fidelity with an additive server change.
- **RQ-3**: *New hub or existing?* Existing `GridEventsHub`. It is authorized, mapped, and consumed; a second hub adds connection overhead and another authorization surface for no benefit.
