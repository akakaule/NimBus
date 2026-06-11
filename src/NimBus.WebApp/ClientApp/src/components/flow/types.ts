// Domain model for the Live Flow page (spec 020, Phase 1). Three modules share
// these shapes: derive-deltas (pure snapshot diffing), layout (pure positioning),
// and the animator/page (rendering). Derivation is deliberately layout-agnostic —
// it emits semantic events keyed by endpoint; the page resolves them onto
// concrete routes via FlowLayout.byEndpoint. Phase 2's per-message `flowactivity`
// hub events will feed the same FlowActivityEvent shape with real attribution.

/**
 * Numeric mirror of api.EndpointStatusCount. The generated client deserializes
 * `eventTime` to a moment instance; pure modules must not depend on moment, so
 * the hook converts to epoch ms at the boundary.
 */
export interface StatusSnapshot {
  endpointId: string;
  failed: number;
  deferred: number;
  pending: number;
  unsupported: number;
  deadlettered: number;
  /** True when storageStatus === "ok" — counts are authoritative. */
  storageOk: boolean;
  subscriptionStatus?: string;
  /** Epoch ms of the server-side EventTime (falls back to receipt time). */
  at: number;
}

/**
 * Semantic activity derived from one snapshot pair (Phase 1) or reported
 * directly by the server (Phase 2).
 *
 * - "arrived":      new messages reached the endpoint (pending rose)
 * - "completed":    pending fell without failures/deferrals rising
 * - "failed":       failed count rose
 * - "deferred":     deferred count rose (messages parked)
 * - "released":     deferred count fell (resubmit or skip — Phase 1 cannot
 *                   tell which, so this advances no counter, only the log)
 * - "deadlettered": deadletter count rose
 */
export type FlowActivityKind =
  | "arrived"
  | "completed"
  | "failed"
  | "deferred"
  | "released"
  | "deadlettered";

export interface FlowActivityEvent {
  endpointId: string;
  kind: FlowActivityKind;
  /** Real magnitude of the change (counter/log use this). */
  count: number;
  /** Dots to actually render: min(count, MAX_DOTS_PER_DELTA). */
  dots: number;
  /** Shown as a "×N" badge when count > dots; 1 otherwise. */
  multiplier: number;
  /** Epoch ms the underlying snapshot reported. */
  at: number;
}

/** Per-endpoint diff result; `baseline` marks the first-ever snapshot. */
export interface EndpointDelta {
  endpointId: string;
  events: FlowActivityEvent[];
  baseline: boolean;
}

// ---------------------------------------------------------------------------
// Layout
// ---------------------------------------------------------------------------

export type FlowNodeKind = "producer" | "topic" | "consumer" | "platform";
export type FlowNodeHealth = "good" | "warn" | "bad" | "idle";

export interface FlowNode {
  /** Unique within the layout: e.g. "producer::CrmEndpoint", "topic::CrmEndpoint". */
  id: string;
  kind: FlowNodeKind;
  /** Endpoint id when the node represents (a column instance of) an endpoint. */
  endpointId?: string;
  title: string;
  /** Secondary line, e.g. system name or event-type count. */
  subtitle?: string;
  x: number;
  y: number;
  w: number;
  h: number;
  health: FlowNodeHealth;
}

export type FlowRouteKind = "publish" | "deliver" | "outcome";

export interface FlowRoute {
  /** Stable: `${kind}::${fromNodeId}::${toNodeId}` — React key + animator handle. */
  id: string;
  kind: FlowRouteKind;
  fromNodeId: string;
  toNodeId: string;
  eventTypeIds: string[];
  /** SVG path data (cubic bezier between node anchors). */
  d: string;
}

/** Animation entry points for one endpoint, resolved once per layout. */
export interface EndpointRouteIndex {
  /** The consumer-column node for this endpoint. */
  nodeId: string;
  /** Routes that bring messages INTO the endpoint (topic → consumer). */
  deliver: string[];
  /** Route carrying outcomes endpoint → Resolver topic. */
  outcome: string;
}

export interface FlowLayout {
  nodes: FlowNode[];
  routes: FlowRoute[];
  /** Canvas extent for the viewBox. */
  width: number;
  height: number;
  byEndpoint: Record<string, EndpointRouteIndex>;
}

// ---------------------------------------------------------------------------
// Page state
// ---------------------------------------------------------------------------

export type ConnectionMode = "connecting" | "live" | "polling";

export interface ActivityEntry {
  id: string;
  at: number;
  endpointId: string;
  kind: FlowActivityKind;
  count: number;
  message: string;
}

/**
 * Counter keys mirror the demo's gauge strip. Seeded from /api/metrics/overview
 * for the selected period; advanced live by derived events (see derive-deltas);
 * reconciled against /api/endpointstatus/all every RECONCILE_MS.
 */
export interface FlowCounters {
  published: number;
  completed: number;
  failed: number;
  deferred: number;
  deadlettered: number;
}

// ---------------------------------------------------------------------------
// Constants (spec FR-007/FR-009; deliberately not user-configurable in v1)
// ---------------------------------------------------------------------------

export const MAX_INFLIGHT_DOTS = 60;
export const MAX_DOTS_PER_DELTA = 8;
export const POLL_MS = 5_000;
export const RECONCILE_MS = 60_000;
export const LOG_MAX = 50;
export const TOP_N_DEFAULT = 12;

/**
 * Status palette — matches the demo's outcome colors so screenshots read the
 * same across the marketing demo and the real page. Used for SVG fills only;
 * chrome around the canvas uses Tailwind semantic tokens.
 */
export const ACTIVITY_COLORS: Record<FlowActivityKind, string> = {
  arrived: "#60a5fa", // blue
  completed: "#34d399", // green
  failed: "#f87171", // red
  deferred: "#fbbf24", // amber
  released: "#22d3ee", // cyan
  deadlettered: "#c084fc", // purple
};
