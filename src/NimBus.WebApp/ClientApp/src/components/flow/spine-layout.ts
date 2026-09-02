// Spine model + edge math for the Flow page's Lanes view (design exploration
// 1b — "route through the event types": publisher → event type → subscriber).
// Pure throughout — no DOM, no clock — so vitest can pin the derivation and
// the port-distribution geometry exactly. The view owns DOM measurement and
// hands measured rects back in; this module turns them into weighted, status-
// colored bezier lanes with hover isolation, the same mechanism the approved
// design prototype used.

import type { TopologyData } from "components/topology/types";

export type SpineHealth = "ok" | "fail" | "idle";

/** Status hues — the design-system values (tailwind `status.*` tokens). */
export const SPINE_COLORS: Record<SpineHealth, string> = {
  ok: "#2E8F5E",
  fail: "#C2412E",
  idle: "#C9C1AB",
};

export interface SpinePublisher {
  /** Node key — `p:<endpointId>`; doubles as the hover-focus handle. */
  key: string;
  endpointId: string;
  name: string;
  /** Published messages per minute, averaged over the window. */
  rate: number;
  /** Distinct event types this endpoint publishes on the visible spine. */
  typeCount: number;
}

export interface SpineTypeNode {
  /** Node key — `t:<eventTypeId>`. */
  key: string;
  eventTypeId: string;
  label: string;
  namespace: string;
  /** Publish-side messages per minute (what lands on the bus). */
  rate: number;
  producers: number;
  consumers: number;
  /** Consumer-side failures over the window (count, not a rate). */
  failed: number;
}

export interface SpineSubscriber {
  /** Node key — `s:<endpointId>`. */
  key: string;
  endpointId: string;
  name: string;
  /** Handled messages per minute, averaged over the window. */
  rate: number;
  /** Failures over the window (count); live snapshots may override in the view. */
  failed: number;
}

export interface SpineLane {
  /** Stable key for React reconciliation. */
  key: string;
  /** Source node key (`p:` for pub hops, `t:` for sub hops). */
  s: string;
  /** Target node key (`t:` for pub hops, `s:` for sub hops). */
  t: string;
  /** Messages per minute on this hop. */
  rate: number;
  health: SpineHealth;
}

export interface SpineModel {
  publishers: SpinePublisher[];
  types: SpineTypeNode[];
  subscribers: SpineSubscriber[];
  lanes: SpineLane[];
  /** Display label per node key — the focus line reads from this. */
  labelByKey: Record<string, string>;
  /** Publish-side messages per minute across every visible type. */
  totalRate: number;
}

export interface SpineModelOptions {
  /** Endpoints to render; undefined = all (same contract as buildFlowLayout). */
  visibleEndpointIds?: ReadonlySet<string>;
  /** Non-empty narrows the spine to one event type's lanes. */
  eventType?: string;
  /** Window length backing the metrics counts, for per-minute rates. */
  periodMinutes: number;
}

/**
 * Derives the three columns and both hop sets from `TopologyData.spine`.
 * Publishers/subscribers sort busiest-first (rate, then name) so the loudest
 * endpoints surface where the operator's eye lands; types keep the data
 * layer's namespace-then-label order (the header says "grouped by namespace").
 */
export function buildSpineModel(
  data: TopologyData,
  opts: SpineModelOptions,
): SpineModel {
  const minutes = Math.max(1, opts.periodMinutes);
  const visible = opts.visibleEndpointIds;
  const isVisible = (id: string): boolean =>
    visible === undefined || visible.has(id);
  const eventType =
    opts.eventType !== undefined && opts.eventType !== ""
      ? opts.eventType
      : undefined;

  const links = data.spine.links.filter(
    (l) =>
      isVisible(l.endpointId) &&
      (eventType === undefined || l.eventTypeId === eventType),
  );
  const linkedTypeIds = new Set(links.map((l) => l.eventTypeId));
  const types = data.spine.types.filter(
    (t) =>
      linkedTypeIds.has(t.id) &&
      (eventType === undefined || t.id === eventType),
  );
  const typeIds = new Set(types.map((t) => t.id));
  const keptLinks = links.filter((l) => typeIds.has(l.eventTypeId));

  // Endpoint aggregates per role, from the surviving hops only — so endpoint
  // cards always reconcile with the lanes actually drawn beside them.
  type EndpointAccum = { rate: number; types: Set<string>; failed: number };
  const pubAccum = new Map<string, EndpointAccum>();
  const subAccum = new Map<string, EndpointAccum>();
  const ensure = (
    map: Map<string, EndpointAccum>,
    id: string,
  ): EndpointAccum => {
    let a = map.get(id);
    if (!a) {
      a = { rate: 0, types: new Set(), failed: 0 };
      map.set(id, a);
    }
    return a;
  };

  const lanes: SpineLane[] = [];
  for (const l of keptLinks) {
    const rate = l.messages / minutes;
    const health: SpineHealth =
      l.failures > 0 ? "fail" : l.messages === 0 ? "idle" : "ok";
    if (l.kind === "pub") {
      const a = ensure(pubAccum, l.endpointId);
      a.rate += rate;
      a.types.add(l.eventTypeId);
      lanes.push({
        key: l.id,
        s: `p:${l.endpointId}`,
        t: `t:${l.eventTypeId}`,
        rate,
        health,
      });
    } else {
      const a = ensure(subAccum, l.endpointId);
      a.rate += rate;
      a.types.add(l.eventTypeId);
      a.failed += l.failures;
      lanes.push({
        key: l.id,
        s: `t:${l.eventTypeId}`,
        t: `s:${l.endpointId}`,
        rate,
        health,
      });
    }
  }

  const publishers: SpinePublisher[] = Array.from(pubAccum.entries())
    .map(([id, a]) => ({
      key: `p:${id}`,
      endpointId: id,
      name: id,
      rate: a.rate,
      typeCount: a.types.size,
    }))
    .sort((a, b) => b.rate - a.rate || compare(a.name, b.name));

  const subscribers: SpineSubscriber[] = Array.from(subAccum.entries())
    .map(([id, a]) => ({
      key: `s:${id}`,
      endpointId: id,
      name: id,
      rate: a.rate,
      failed: a.failed,
    }))
    .sort((a, b) => b.rate - a.rate || compare(a.name, b.name));

  const typeNodes: SpineTypeNode[] = types.map((t) => ({
    key: `t:${t.id}`,
    eventTypeId: t.id,
    label: t.label,
    namespace: t.namespace,
    rate: t.published / minutes,
    producers: t.producers,
    consumers: t.consumers,
    failed: t.failed,
  }));

  const labelByKey: Record<string, string> = {};
  for (const p of publishers) labelByKey[p.key] = p.name;
  for (const t of typeNodes) labelByKey[t.key] = t.label;
  for (const s of subscribers) labelByKey[s.key] = s.name;

  let totalRate = 0;
  for (const t of typeNodes) totalRate += t.rate;

  return {
    publishers,
    types: typeNodes,
    subscribers,
    lanes,
    labelByKey,
    totalRate,
  };
}

// ---------------------------------------------------------------------------
// Edge geometry — port of the approved prototype's build(): distributed ports
// sorted by peer position, weighted curves, hover isolation, focus rings.
// ---------------------------------------------------------------------------

export interface SpineRect {
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface SpineEdgePath {
  key: string;
  d: string;
  color: string;
  /** Base stroke width — messages per minute on a square-root scale. */
  w: number;
  op: number;
  /** Marching-dash overlay stroke width / opacity (0 disables the overlay). */
  dw: number;
  dop: number;
}

export interface SpineRing {
  key: string;
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface SpineEdgeOptions {
  /** Hovered node key; null renders the resting state. */
  focus: string | null;
  /** Resting lane opacity (design default 0.42). */
  restOpacity?: number;
  /** False (e.g. prefers-reduced-motion) removes the marching-dash overlay. */
  animate?: boolean;
}

const REST_OPACITY = 0.42;
const MAX_STROKE = 10;

/** Lane stroke width from per-minute rate — the design's square-root scale. */
export function laneWidth(rate: number): number {
  return Math.min(MAX_STROKE, 1.1 + Math.sqrt(Math.max(0, rate)) / 5.2);
}

/**
 * Resolves lanes onto measured node rects. Ports leave/enter each card at
 * distinct points along its edge, sorted by the peer's vertical position, so
 * lanes fan out instead of stacking into a single tangent (design bullet 3).
 * Hovering a node keeps its lanes at 0.95 opacity, drops the rest to a faint
 * trace, and rings every touched node in coral.
 */
export function computeSpineEdges(
  rects: Readonly<Record<string, SpineRect>>,
  lanes: readonly SpineLane[],
  opts: SpineEdgeOptions,
): { edges: SpineEdgePath[]; rings: SpineRing[] } {
  const rest = opts.restOpacity ?? REST_OPACITY;
  const animate = opts.animate !== false;
  const focus = opts.focus;

  const centerY = (key: string): number => {
    const r = rects[key];
    return r === undefined ? 0 : r.y + r.h / 2;
  };
  const bySource = new Map<string, SpineLane[]>();
  const byTarget = new Map<string, SpineLane[]>();
  for (const lane of lanes) {
    if (rects[lane.s] === undefined || rects[lane.t] === undefined) continue;
    let s = bySource.get(lane.s);
    if (!s) bySource.set(lane.s, (s = []));
    s.push(lane);
    let t = byTarget.get(lane.t);
    if (!t) byTarget.set(lane.t, (t = []));
    t.push(lane);
  }
  for (const group of bySource.values()) {
    group.sort((a, b) => centerY(a.t) - centerY(b.t) || compare(a.key, b.key));
  }
  for (const group of byTarget.values()) {
    group.sort((a, b) => centerY(a.s) - centerY(b.s) || compare(a.key, b.key));
  }

  const edges: SpineEdgePath[] = [];
  const touched = new Set<string>();
  for (const lane of lanes) {
    const a = rects[lane.s];
    const b = rects[lane.t];
    if (a === undefined || b === undefined) continue;
    const sourceGroup = bySource.get(lane.s)!;
    const targetGroup = byTarget.get(lane.t)!;
    const si = sourceGroup.indexOf(lane);
    const ti = targetGroup.indexOf(lane);
    const x1 = a.x + a.w;
    const y1 = a.y + (a.h * (si + 1)) / (sourceGroup.length + 1);
    const x2 = b.x;
    const y2 = b.y + (b.h * (ti + 1)) / (targetGroup.length + 1);
    const dx = Math.max(60, (x2 - x1) * 0.45);

    const on = focus !== null && (focus === lane.s || focus === lane.t);
    if (on) {
      touched.add(lane.s);
      touched.add(lane.t);
    }
    const w = laneWidth(lane.rate);
    edges.push({
      key: lane.key,
      d: `M ${r1(x1)} ${r1(y1)} C ${r1(x1 + dx)} ${r1(y1)}, ${r1(x2 - dx)} ${r1(y2)}, ${r1(x2)} ${r1(y2)}`,
      color: SPINE_COLORS[lane.health],
      w,
      op: focus !== null ? (on ? 0.95 : rest * 0.16) : rest,
      dw: Math.max(1.2, w * 0.55),
      dop: animate ? (focus !== null ? (on ? 1 : 0) : 0.85) : 0,
    });
  }

  const rings: SpineRing[] = Array.from(touched)
    .sort(compare)
    .map((key) => {
      const r = rects[key]!;
      return { key, x: r.x - 4, y: r.y - 4, w: r.w + 8, h: r.h + 8 };
    });

  return { edges, rings };
}

/**
 * Footer line for the hovered node — column-aware phrasing so the sentence
 * always names what actually happens there (publish, carry, or handle).
 */
export function spineFocusLine(
  model: SpineModel,
  focus: string | null,
): string {
  if (focus === null) return "";
  const label = model.labelByKey[focus] ?? focus;
  const ins = model.lanes.filter((l) => l.t === focus);
  const outs = model.lanes.filter((l) => l.s === focus);
  const sum = (group: SpineLane[]): number =>
    group.reduce((acc, l) => acc + l.rate, 0);
  const failing = sum([...ins, ...outs].filter((l) => l.health === "fail"));

  const parts = [label];
  if (focus.startsWith("p:")) {
    parts.push(
      `publishes ${formatRate(sum(outs))}/min across ${count(outs.length, "type")}`,
    );
  } else if (focus.startsWith("t:")) {
    if (ins.length > 0) {
      parts.push(
        `receives ${formatRate(sum(ins))}/min from ${count(ins.length, "publisher")}`,
      );
    }
    if (outs.length > 0) {
      parts.push(
        `delivers ${formatRate(sum(outs))}/min to ${count(outs.length, "subscriber")}`,
      );
    }
  } else {
    parts.push(
      `receives ${formatRate(sum(ins))}/min from ${count(ins.length, "type")}`,
    );
  }
  if (failing > 0) parts.push(`${formatRate(failing)}/min failing`);
  return parts.join(" · ");
}

/**
 * Rate formatter — the design's k-notation for thousands; sub-10 rates keep
 * one decimal so a trickle doesn't display as zero.
 */
export function formatRate(rate: number): string {
  if (rate >= 1000) return `${(rate / 1000).toFixed(2).replace(/0$/, "")}k`;
  if (rate >= 10) return String(Math.round(rate));
  if (rate > 0) return String(Math.max(0.1, Math.round(rate * 10) / 10));
  return "0";
}

function count(n: number, noun: string): string {
  return `${n} ${noun}${n === 1 ? "" : "s"}`;
}

function r1(value: number): number {
  return Math.round(value * 10) / 10;
}

function compare(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}
