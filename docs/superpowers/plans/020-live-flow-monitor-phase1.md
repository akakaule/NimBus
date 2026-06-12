# Implementation Plan: Live Flow Monitor — Phase 1

Spec: `docs/specs/020-live-flow-monitor/spec.md` (Phase 1 scope only: zero server changes)
Branch: `020-live-flow-monitor`
Created: 2026-06-11

## Context discovered during planning

- `@microsoft/signalr@^8` is already in `ClientApp/package.json` but **nothing in the SPA uses it today** — the hub broadcasts server-side with no client consumer. Phase 1 therefore introduces the first client-side hub connection, built as a shared module (`lib/grid-events-connection.ts`) so later pages can reuse it (satisfies the spec's "single connection" assumption via the refactor path).
- `components/topology/types.ts` + `use-topology-data.ts` already compute `FlowEdge[]` (producer→consumer routes with event types and traffic) — the layout engine consumes `TopologyData` directly instead of re-fetching the catalog.
- Hub path `/hubs/gridevents`, event name `endpointupdate`, payload `api.EndpointStatusCount` (`endpointId`, `failedCount`, `deferredCount`, `pendingCount`, `unsupportedCount`, `deadletterCount`, `storageStatus`, `subscriptionStatus`, `eventTime`).
- Conventions: routes declared in `app.tsx` `navigation` array (lazy pages), sidebar nav in `sidebar.tsx` `NAV` groups, polling pattern in `hooks/use-monitor-data.ts` (5 s cadence, refs for per-tick state, localStorage persistence), vitest with `environment: 'jsdom'` in `vite.config.ts`, path aliases rooted at `src/` ("components/…", "hooks/…", "api-client").

## Architecture (client-only)

```
use-topology-data ──► layout.ts ──► FlowLayout {nodes, routes, routesByEndpoint index}
                                            │ (static SVG, React-rendered)
grid-events-connection ─► use-flow-data ──► FlowActivityEvent[] ──► page resolves to routes ──► FlowAnimator (imperative <g> layer)
        ▲ endpointupdate      │ derive-deltas.ts (pure)                                              │ dots, pulses, parking
        └ polling fallback    └ counters / activity log / connection mode (React state, throttled)
```

Separation rule: **derivation is layout-agnostic** — it emits semantic `FlowActivityEvent`s keyed by endpoint; the page maps them onto concrete routes via the layout's `routesByEndpoint` index. This keeps the pure modules independently testable and lets Phase 2's `flowactivity` events feed the same renderer.

## File map

| # | File | Contents |
|---|---|---|
| 1 | `ClientApp/src/components/flow/types.ts` | Contracts (below) + constants + status palette |
| 2 | `ClientApp/src/components/flow/derive-deltas.ts` (+ `.test.ts`) | Pure: snapshot diff → `FlowActivityEvent[]` per spec FR-004 table |
| 3 | `ClientApp/src/components/flow/layout.ts` (+ `.test.ts`) | Pure: `TopologyData` → positioned layered layout + route paths |
| 4 | `ClientApp/src/components/flow/animator.ts` | Imperative rAF dot engine (no React imports) |
| 5 | `ClientApp/src/lib/grid-events-connection.ts` | Shared SignalR connection wrapper |
| 6 | `ClientApp/src/hooks/use-flow-data.ts` | Orchestration hook (subscribe/poll/reconcile/log/counters) |
| 7 | `ClientApp/src/pages/flow.tsx` | Page: canvas, controls, sidebar |
| 8 | `app.tsx`, `components/sidebar.tsx` | Route `/Flow` + Observe nav entry |

## Key contracts (authored first, in types.ts)

- `StatusSnapshot` — numeric mirror of `EndpointStatusCount` (no moment.js in pure modules).
- `EndpointDelta` / `FlowActivityEvent { endpointId, kind: "arrived"|"completed"|"failed"|"deferred"|"released"|"deadlettered", count, dots, multiplier, at }`.
- `FlowNode { id, kind: "producer"|"topic"|"consumer"|"platform", x, y, w, h, title, endpointId?, health }`.
- `FlowRoute { id, kind: "publish"|"deliver"|"outcome", fromNodeId, toNodeId, eventTypeIds, d }`.
- `FlowLayout { nodes, routes, width, height, byEndpoint: Record<endpointId, { deliver: routeIds[], outcome: routeId, nodeId }> }`.
- Constants: `MAX_INFLIGHT_DOTS = 60`, `MAX_DOTS_PER_DELTA = 8`, `POLL_MS = 5000`, `RECONCILE_MS = 60_000`, `LOG_MAX = 50`, `TOP_N_DEFAULT = 12`.

## Honest-fidelity rules (from spec FR-004)

First snapshot per endpoint = baseline (recorded, never animated). Counter advancement from deltas: `arrived→published`, `completed`, `failed`, `deferred`, `deadlettered`; `released` (deferred −N) appears in the log but does not guess resubmit-vs-skip for counters — reconciliation (60 s against `/api/endpointstatus/all`, period counters from `/api/metrics/overview`) corrects drift.

## Task order

1. Plan + types (this doc) — me.
2. ⇉ In parallel (disjoint new files): derive-deltas (+tests), layout (+tests), animator — one subagent each, briefed on conventions below.
3. Connection lib + hook + page + nav wiring (depends on 2).
4. Verify: `npx vitest run`, `npm run build` (tsc + vite) in `src/NimBus.WebApp/ClientApp` (exact casing), existing tests stay green. Local commits only; no push.

## Subagent brief (applies to all implementation tasks)

- Working dir for npm/vitest: `c:\Git\NimBus-Master\NimBus\src\NimBus.WebApp\ClientApp` — exact case.
- TypeScript strict; imports via `src/`-rooted aliases; **no new npm dependencies**.
- Match the codebase's comment style: explanatory block comments stating *why* (see `use-monitor-data.ts`).
- Tailwind semantic tokens (`bg-background`, theme-aware) — no hardcoded hex in JSX; SVG fills may use the status palette from `types.ts`.
- Vitest, `environment: jsdom`; test files co-located (`*.test.ts`).
