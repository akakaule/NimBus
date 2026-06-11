import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Link } from "react-router-dom";
import * as api from "api-client";
import Page from "components/page";
import { Button } from "components/ui/button";
import { Checkbox } from "components/ui/checkbox";
import { EmptyState } from "components/ui/empty-state";
import { Select } from "components/ui/select";
import { Spinner } from "components/ui/spinner";
import { cn } from "lib/utils";
import { FlowAnimator } from "components/flow/animator";
import { buildFlowLayout, topEndpointIds } from "components/flow/layout";
import { ACTIVITY_COLORS, TOP_N_DEFAULT } from "components/flow/types";
import type {
  ActivityEntry,
  ConnectionMode,
  FlowActivityEvent,
  FlowActivityKind,
  FlowLayout,
  FlowNode,
  StatusSnapshot,
} from "components/flow/types";
import type { TopologyData, TopologyNode } from "components/topology/types";
import { FLOW_KIND_VERB, useFlowData } from "hooks/use-flow-data";

// Live Flow page (spec 020, Phase 1). React owns the static scene — nodes,
// route paths, parking strips — rebuilt declaratively when topology/filters
// change; the FlowAnimator owns exactly one <g> (the dot layer) and animates
// derived activity imperatively so per-frame work never touches React
// reconciliation (NFR-002). Data plumbing lives in useFlowData; this file is
// layout resolution (semantic event → concrete routes) plus chrome.

const PERIODS: Array<{ label: string; value: api.Period }> = [
  { label: "1h", value: api.Period._1h },
  { label: "12h", value: api.Period._12h },
  { label: "1d", value: api.Period._1d },
  { label: "7d", value: api.Period._7d },
];

// ---------------------------------------------------------------------------
// Persisted preferences (OQ-2 resolution: persist filter + speed)
// ---------------------------------------------------------------------------

const PREFS_KEY = "nb.flow.v1";

interface FlowPrefs {
  speed: number;
  /**
   * Explicit endpoint selection. Three-state on purpose:
   *  - string[]  — the user hand-picked endpoints
   *  - null      — the user chose "Show all"
   *  - undefined — never chosen; the page applies the FR-007 default
   *                (top TOP_N_DEFAULT by traffic when the catalog is bigger)
   */
  endpointIds?: string[] | null;
}

function loadPrefs(): FlowPrefs {
  if (typeof window === "undefined") return { speed: 1 };
  try {
    const raw = window.localStorage.getItem(PREFS_KEY);
    if (!raw) return { speed: 1 };
    const parsed = JSON.parse(raw) as Partial<FlowPrefs> | null;
    if (!parsed || typeof parsed !== "object") return { speed: 1 };
    const speed =
      typeof parsed.speed === "number" &&
      parsed.speed >= 0.25 &&
      parsed.speed <= 4
        ? parsed.speed
        : 1;
    const endpointIds = Array.isArray(parsed.endpointIds)
      ? parsed.endpointIds.filter((id): id is string => typeof id === "string")
      : parsed.endpointIds === null
        ? null
        : undefined;
    return { speed, endpointIds };
  } catch {
    return { speed: 1 };
  }
}

function savePrefs(prefs: FlowPrefs): void {
  if (typeof window === "undefined") return;
  try {
    window.localStorage.setItem(PREFS_KEY, JSON.stringify(prefs));
  } catch {
    // localStorage can be unavailable (private mode, quota); ignore.
  }
}

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export default function Flow() {
  const [period, setPeriod] = useState<api.Period>(api.Period._1h);
  const [paused, setPaused] = useState(false);
  const [speed, setSpeed] = useState<number>(() => loadPrefs().speed);
  const [selection, setSelection] = useState<string[] | null | undefined>(
    () => loadPrefs().endpointIds,
  );
  const [eventType, setEventType] = useState("");

  // The animator and the layout index are read from a STABLE callback (the
  // hook holds onActivity for the lifetime of the subscription), so both live
  // in refs that the render path keeps current.
  const dotLayerRef = useRef<SVGGElement | null>(null);
  const routePathRefs = useRef(new Map<string, SVGPathElement>());
  const animatorRef = useRef<FlowAnimator | null>(null);
  const layoutRef = useRef<FlowLayout | undefined>(undefined);

  /**
   * Resolves semantic activity onto concrete routes and spawns dots:
   * arrived/released ride the deliver routes (round-robin when an endpoint
   * has several upstream topics), every outcome kind rides the endpoint →
   * Resolver route. Events for endpoints that are filtered out or unknown to
   * the catalog are skipped silently — the hook already recorded them in the
   * log/counters. The ×N badge goes on the FIRST dot only (FR-009).
   */
  const handleActivity = useCallback((events: FlowActivityEvent[]) => {
    const layout = layoutRef.current;
    const animator = animatorRef.current;
    if (layout === undefined || animator === null) return;
    for (const event of events) {
      const index = layout.byEndpoint[event.endpointId];
      if (index === undefined) continue;
      const routes =
        event.kind === "arrived" || event.kind === "released"
          ? index.deliver
          : [index.outcome];
      if (routes.length === 0) continue;
      for (let i = 0; i < event.dots; i++) {
        animator.spawn(routes[i % routes.length], ACTIVITY_COLORS[event.kind], {
          multiplier: i === 0 ? event.multiplier : 1,
        });
      }
    }
  }, []);

  const { topology, topologyLoading, topologyError, counters, log, mode, snapshots } =
    useFlowData({ period, paused, onActivity: handleActivity });

  // FR-007 default: top N busiest endpoints when the catalog is bigger than
  // N; everything otherwise. Only applies until the user makes an explicit
  // choice (persisted across visits).
  const effectiveSelection = useMemo<string[] | null>(() => {
    if (selection !== undefined) return selection;
    if (topology !== undefined && topology.nodes.length > TOP_N_DEFAULT) {
      return topEndpointIds(topology, TOP_N_DEFAULT);
    }
    return null;
  }, [selection, topology]);

  const visibleSet = useMemo(
    () => (effectiveSelection === null ? undefined : new Set(effectiveSelection)),
    [effectiveSelection],
  );

  // Event-type filtering happens BEFORE layout: drop non-matching flowEdges
  // from a shallow copy so the layout engine simply never sees those routes.
  const filteredData = useMemo<TopologyData | undefined>(() => {
    if (topology === undefined || eventType === "") return topology;
    return {
      ...topology,
      flowEdges: topology.flowEdges.filter((e) =>
        e.eventTypeIds.includes(eventType),
      ),
    };
  }, [topology, eventType]);

  const layout = useMemo<FlowLayout | undefined>(() => {
    if (filteredData === undefined || filteredData.nodes.length === 0) {
      return undefined;
    }
    return buildFlowLayout(filteredData, { visibleEndpointIds: visibleSet });
  }, [filteredData, visibleSet]);
  layoutRef.current = layout;

  const eventTypeOptions = useMemo(() => {
    if (topology === undefined) return [];
    const out = new Set<string>();
    for (const edge of topology.flowEdges) {
      for (const id of edge.eventTypeIds) out.add(id);
    }
    return Array.from(out).sort();
  }, [topology]);

  // Animator lifecycle — created once the canvas exists, torn down with the
  // page. Keyed on a boolean so layout-to-layout changes (filters) reuse the
  // same instance; invalidatePaths below handles re-rendered paths.
  const canvasReady = layout !== undefined;
  useEffect(() => {
    if (!canvasReady) return;
    const dotLayer = dotLayerRef.current;
    if (dotLayer === null) return;
    const animator = new FlowAnimator({
      dotLayer,
      getPathEl: (id) => routePathRefs.current.get(id) ?? null,
      reducedMotion: window.matchMedia("(prefers-reduced-motion: reduce)")
        .matches,
    });
    animatorRef.current = animator;
    animator.start();
    return () => {
      animator.dispose();
      if (animatorRef.current === animator) animatorRef.current = null;
    };
  }, [canvasReady]);

  // Declared after the creation effect so they also run on the commit that
  // instantiates the animator (applying the persisted speed immediately).
  useEffect(() => {
    animatorRef.current?.setSpeed(speed);
  }, [speed, canvasReady]);
  useEffect(() => {
    animatorRef.current?.setPaused(paused);
  }, [paused, canvasReady]);

  // Filters/topology re-render the route paths — drop memoized lengths so
  // new dots measure the new geometry (in-flight dots keep their own).
  useEffect(() => {
    animatorRef.current?.invalidatePaths();
  }, [layout]);

  useEffect(() => {
    savePrefs({ speed, endpointIds: selection });
  }, [speed, selection]);

  // Coarse clock for the activity log's relative timestamps — 30 s keeps
  // them honest without re-rendering the static SVG every second.
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    const handle = window.setInterval(() => setNow(Date.now()), 30_000);
    return () => window.clearInterval(handle);
  }, []);

  const visibleCount = useMemo(() => {
    if (topology === undefined) return 0;
    if (effectiveSelection === null) return topology.nodes.length;
    const known = new Set(topology.nodes.map((n) => n.id));
    return effectiveSelection.filter((id) => known.has(id)).length;
  }, [topology, effectiveSelection]);

  return (
    <Page
      title="Flow"
      subtitle="Live message flow across producers, topics, and endpoints — derived from real-time status deltas."
      actions={
        <>
          <ModeBadge mode={mode} />
          <div className="inline-flex items-center bg-card border border-border rounded-nb-md p-[3px] gap-[2px]">
            {PERIODS.map((p) => (
              <button
                key={p.value}
                onClick={() => setPeriod(p.value)}
                className={cn(
                  "px-3 py-1.5 rounded-md text-xs font-semibold transition-colors",
                  period === p.value
                    ? "bg-primary text-white"
                    : "text-muted-foreground hover:text-foreground",
                )}
              >
                {p.label}
              </button>
            ))}
          </div>
        </>
      }
    >
      <div className="w-full flex flex-col gap-4">
        {layout !== undefined && topology !== undefined ? (
          <>
            <div className="flex flex-wrap items-center gap-2.5">
              <Button
                variant="ghost"
                size="sm"
                onClick={() => setPaused((p) => !p)}
                title={paused ? "Resume the animation" : "Freeze dots in place"}
              >
                {paused ? "▶ Resume" : "❚❚ Pause"}
              </Button>
              <label className="inline-flex items-center gap-2 h-8 px-3 bg-card border border-border-strong rounded-nb-md text-xs text-muted-foreground">
                Speed
                <input
                  type="range"
                  min={0.25}
                  max={4}
                  step={0.25}
                  value={speed}
                  onChange={(e) => setSpeed(Number(e.target.value))}
                  className="w-28 accent-primary"
                  aria-label="Animation speed"
                />
                <span className="font-mono text-[11px] w-10 text-right text-foreground">
                  {formatSpeed(speed)}×
                </span>
              </label>
              <EndpointFilterMenu
                nodes={topology.nodes}
                selection={effectiveSelection}
                visibleCount={visibleCount}
                onChange={setSelection}
                onTopN={() =>
                  setSelection(topEndpointIds(topology, TOP_N_DEFAULT))
                }
                onShowAll={() => setSelection(null)}
              />
              <Select
                value={eventType}
                onChange={(e) => setEventType(e.target.value)}
                className="h-8 w-auto max-w-[240px] py-0 text-xs"
                aria-label="Event type filter"
              >
                <option value="">All event types</option>
                {eventTypeOptions.map((id) => (
                  <option key={id} value={id}>
                    {id}
                  </option>
                ))}
              </Select>
            </div>

            <div className="grid grid-cols-1 lg:grid-cols-[1fr_320px] gap-4 items-start">
              <div className="bg-card border border-border rounded-nb-lg p-4 overflow-x-auto min-w-0 text-foreground">
                <FlowStyles />
                <svg
                  viewBox={`0 0 ${layout.width} ${layout.height}`}
                  className="w-full h-auto min-w-[900px]"
                  role="img"
                  aria-label="Live message flow diagram"
                >
                  {layout.routes.map((route) => (
                    <path
                      key={route.id}
                      d={route.d}
                      className="flow-route"
                      data-kind={route.kind}
                      ref={(el) => {
                        if (el) routePathRefs.current.set(route.id, el);
                        else routePathRefs.current.delete(route.id);
                      }}
                    />
                  ))}
                  {layout.nodes.map((node) => (
                    <FlowNodeGroup
                      key={node.id}
                      node={node}
                      snapshot={
                        node.endpointId !== undefined
                          ? snapshots[node.endpointId]
                          : undefined
                      }
                    />
                  ))}
                  {/* Dot layer LAST so traveling dots draw above everything;
                      owned exclusively by the FlowAnimator. */}
                  <g ref={dotLayerRef} />
                </svg>
                <p className="m-0 mt-2 text-[11px] italic text-muted-foreground">
                  Dots are derived from count deltas — representative activity,
                  not individual messages.
                </p>
              </div>

              <aside className="hidden lg:flex flex-col gap-4 min-w-0">
                <div className="grid grid-cols-2 gap-2">
                  <CounterGauge
                    label="Published"
                    value={counters.published}
                    color={ACTIVITY_COLORS.arrived}
                  />
                  <CounterGauge
                    label="Completed"
                    value={counters.completed}
                    color={ACTIVITY_COLORS.completed}
                  />
                  <CounterGauge
                    label="Failed"
                    value={counters.failed}
                    color={ACTIVITY_COLORS.failed}
                  />
                  <CounterGauge
                    label="Deferred"
                    value={counters.deferred}
                    color={ACTIVITY_COLORS.deferred}
                  />
                  <CounterGauge
                    label="Dead-lettered"
                    value={counters.deadlettered}
                    color={ACTIVITY_COLORS.deadlettered}
                  />
                </div>
                <FlowLegend />
                <ActivityLog log={log} now={now} />
              </aside>
            </div>
          </>
        ) : topologyLoading ? (
          <div className="flex items-center justify-center h-[400px] w-full">
            <Spinner size="xl" color="primary" />
          </div>
        ) : (
          <EmptyState
            icon="◌"
            title={topologyError ?? "No endpoints yet"}
            description="Register an event type with at least one producer or consumer to watch messages flow."
          />
        )}
      </div>
    </Page>
  );
}

// ---------------------------------------------------------------------------
// Canvas pieces
// ---------------------------------------------------------------------------

const PARK_DOT_MAX = 6;

/**
 * One static node. Consumer nodes carry the live decorations: the deferred
 * parking strip reads the authoritative snapshot count (FR-006 — present on
 * first paint, no animation required), the attention ring lights while the
 * latest snapshot reports failures, and a degraded border with a tooltip
 * flags storage-unavailable endpoints (US2).
 */
const FlowNodeGroup = ({
  node,
  snapshot,
}: {
  node: FlowNode;
  snapshot: StatusSnapshot | undefined;
}) => {
  const isConsumer = node.kind === "consumer";
  const attention = isConsumer && snapshot !== undefined && snapshot.failed > 0;
  const degraded = isConsumer && snapshot !== undefined && !snapshot.storageOk;
  const deferred = isConsumer && snapshot !== undefined ? snapshot.deferred : 0;
  const isChip = node.kind === "topic";
  const titleY = isChip
    ? node.y + 17
    : node.kind === "platform"
      ? node.y + node.h / 2 + 4
      : node.y + 24;

  return (
    <g>
      <rect
        x={node.x}
        y={node.y}
        width={node.w}
        height={node.h}
        rx={isChip ? 6 : 9}
        className={cn(
          "flow-node",
          `flow-node-${node.kind}`,
          attention && "flow-attention",
          degraded && "flow-degraded",
        )}
      >
        {degraded && (
          <title>
            Storage unavailable — counts for this endpoint may be stale or
            zero.
          </title>
        )}
      </rect>
      <text
        x={node.x + 12}
        y={titleY}
        className={isChip ? "flow-chip-title" : "flow-node-title"}
      >
        {truncate(node.title, isChip ? 34 : 26)}
      </text>
      {node.subtitle !== undefined && (
        <text
          x={node.x + 12}
          y={isChip ? node.y + 30 : node.y + 38}
          className={isChip ? "flow-chip-subtitle" : "flow-node-subtitle"}
        >
          {truncate(node.subtitle, isChip ? 38 : 30)}
        </text>
      )}
      {deferred > 0 && (
        <g aria-label={`${deferred} deferred messages parked`}>
          {Array.from({ length: Math.min(deferred, PARK_DOT_MAX) }, (_, i) => (
            <circle
              key={i}
              cx={node.x + 16 + i * 10}
              cy={node.y + node.h - 12}
              r={3.5}
              className="flow-park-dot"
            />
          ))}
          <text
            x={node.x + 16 + Math.min(deferred, PARK_DOT_MAX) * 10 + 6}
            y={node.y + node.h - 8.5}
            className="flow-park-label"
          >
            {deferred.toLocaleString()} parked
          </text>
        </g>
      )}
    </g>
  );
};

/**
 * Page-scoped SVG styling (all class names flow-prefixed). Node fills use
 * low-alpha rgb so the same values read on both the cream and warm-black
 * themes; text uses currentColor inherited from the card's text-foreground.
 * Dot/parking colors come from ACTIVITY_COLORS to stay in lockstep with the
 * legend and the animator.
 */
const FlowStyles = () => (
  <style>{`
    .flow-route {
      fill: none;
      stroke: rgb(100 116 139 / 0.9);
      stroke-width: 1.5;
      opacity: 0.22;
      transition: opacity 0.25s ease;
    }
    .flow-route[data-kind="outcome"] { stroke-dasharray: 4 5; }
    .flow-route-on { opacity: 0.95; }

    .flow-node { stroke-width: 1.25; }
    .flow-node-producer { fill: rgb(14 165 233 / 0.10); stroke: #0ea5e9; }
    .flow-node-topic { fill: rgb(100 116 139 / 0.12); stroke: rgb(100 116 139 / 0.8); }
    .flow-node-consumer { fill: rgb(20 184 166 / 0.10); stroke: #14b8a6; }
    .flow-node-platform { fill: rgb(148 163 184 / 0.10); stroke: #94a3b8; }

    .flow-degraded { stroke: ${ACTIVITY_COLORS.failed}; stroke-dasharray: 5 3; }
    .flow-attention { animation: flow-attention-pulse 1.6s ease-in-out infinite; }
    @keyframes flow-attention-pulse {
      0%, 100% { stroke: ${ACTIVITY_COLORS.failed}; stroke-width: 1.5; }
      50% { stroke: #ef4444; stroke-width: 3; }
    }

    .flow-node-title { font-size: 12px; font-weight: 600; fill: currentColor; }
    .flow-node-subtitle { font-size: 10px; fill: currentColor; opacity: 0.55; }
    .flow-chip-title { font-size: 10.5px; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; fill: currentColor; opacity: 0.8; }
    .flow-chip-subtitle { font-size: 9px; fill: currentColor; opacity: 0.5; }
    .flow-dot-label { font-size: 10px; font-weight: 700; fill: currentColor; }
    .flow-park-dot { fill: ${ACTIVITY_COLORS.deferred}; }
    .flow-park-label { font-size: 9.5px; font-weight: 700; fill: #d97706; }
  `}</style>
);

// ---------------------------------------------------------------------------
// Controls
// ---------------------------------------------------------------------------

const ModeBadge = ({ mode }: { mode: ConnectionMode }) => (
  <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full border border-border bg-card font-mono text-[11px] text-muted-foreground whitespace-nowrap">
    <span
      aria-hidden="true"
      className={cn(
        "w-2 h-2 rounded-full",
        mode === "live" && "bg-emerald-500",
        mode === "polling" && "bg-amber-500",
        mode === "connecting" && "bg-slate-400 animate-pulse",
      )}
    />
    {mode === "live"
      ? "Live"
      : mode === "polling"
        ? "Degraded — polling"
        : "Connecting"}
  </span>
);

interface EndpointFilterMenuProps {
  nodes: TopologyNode[];
  /** Currently effective selection; null = all endpoints visible. */
  selection: string[] | null;
  visibleCount: number;
  onChange: (next: string[]) => void;
  onTopN: () => void;
  onShowAll: () => void;
}

// Dropdown panel with per-endpoint checkboxes plus the FR-007 quick actions.
// Same lightweight popover pattern as the Topology page's AddFilterMenu:
// local open state, click-outside to close.
const EndpointFilterMenu = ({
  nodes,
  selection,
  visibleCount,
  onChange,
  onTopN,
  onShowAll,
}: EndpointFilterMenuProps) => {
  const [open, setOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!open) return;
    const handleClick = (event: MouseEvent) => {
      if (
        containerRef.current &&
        !containerRef.current.contains(event.target as Node)
      ) {
        setOpen(false);
      }
    };
    document.addEventListener("mousedown", handleClick);
    return () => document.removeEventListener("mousedown", handleClick);
  }, [open]);

  const isChecked = (id: string): boolean =>
    selection === null || selection.includes(id);
  const toggle = (id: string): void => {
    const base = selection ?? nodes.map((n) => n.id);
    onChange(
      base.includes(id) ? base.filter((x) => x !== id) : [...base, id],
    );
  };

  return (
    <div ref={containerRef} className="relative">
      <Button variant="ghost" size="sm" onClick={() => setOpen((o) => !o)}>
        Endpoints · {visibleCount}/{nodes.length}
      </Button>
      {open && (
        <div
          className={cn(
            "absolute z-10 mt-1 left-0",
            "bg-card border border-border rounded-nb-md shadow-md",
            "min-w-[280px] max-h-[360px] overflow-y-auto p-2",
          )}
          role="menu"
        >
          <div className="flex gap-1.5 pb-2 mb-2 border-b border-border">
            <Button variant="quiet" size="xs" onClick={onTopN}>
              Top {TOP_N_DEFAULT}
            </Button>
            <Button variant="quiet" size="xs" onClick={onShowAll}>
              Show all
            </Button>
          </div>
          <div className="flex flex-col gap-1.5">
            {nodes.map((node) => (
              <Checkbox
                key={node.id}
                label={node.name}
                checked={isChecked(node.id)}
                onChange={() => toggle(node.id)}
              />
            ))}
          </div>
        </div>
      )}
    </div>
  );
};

// ---------------------------------------------------------------------------
// Sidebar
// ---------------------------------------------------------------------------

const CounterGauge = ({
  label,
  value,
  color,
}: {
  label: string;
  value: number;
  color: string;
}) => (
  <div className="bg-card border border-border rounded-nb-md px-3 py-2.5">
    <div className="font-mono text-[10px] uppercase tracking-wider text-muted-foreground">
      {label}
    </div>
    <div className="text-lg font-bold tabular-nums" style={{ color }}>
      {value.toLocaleString()}
    </div>
  </div>
);

const LEGEND_KINDS: Array<{ kind: FlowActivityKind; label: string }> = [
  { kind: "arrived", label: "Arrived" },
  { kind: "completed", label: "Completed" },
  { kind: "failed", label: "Failed" },
  { kind: "deferred", label: "Deferred" },
  { kind: "released", label: "Released" },
  { kind: "deadlettered", label: "Dead-lettered" },
];

const FlowLegend = () => (
  <div className="bg-card border border-border rounded-nb-md px-3 py-2.5 flex flex-wrap gap-x-4 gap-y-1.5">
    {LEGEND_KINDS.map(({ kind, label }) => (
      <span
        key={kind}
        className="inline-flex items-center gap-1.5 text-[11px] text-muted-foreground"
      >
        <span
          aria-hidden="true"
          className="w-2.5 h-2.5 rounded-full"
          style={{ background: ACTIVITY_COLORS[kind] }}
        />
        {label}
      </span>
    ))}
  </div>
);

const ActivityLog = ({
  log,
  now,
}: {
  log: ActivityEntry[];
  now: number;
}) => (
  <div className="bg-card border border-border rounded-nb-md p-3 flex flex-col gap-2 min-h-0">
    <div className="font-mono text-[10px] uppercase tracking-wider text-muted-foreground">
      Activity
    </div>
    {log.length === 0 ? (
      <p className="text-[12px] text-muted-foreground m-0">
        Waiting for endpoint activity…
      </p>
    ) : (
      <ul className="m-0 p-0 list-none flex flex-col gap-1.5 overflow-y-auto max-h-[420px]">
        {log.map((entry) => (
          <li
            key={entry.id}
            className="flex items-baseline gap-2 text-[12px] leading-snug"
          >
            <span className="font-mono text-[10px] text-muted-foreground shrink-0 w-9 text-right">
              {relativeAgo(Math.max(0, now - entry.at))}
            </span>
            <span
              aria-hidden="true"
              className={cn(
                "w-1.5 h-1.5 rounded-full shrink-0",
                entry.endpointId === "" && "bg-muted-foreground/40",
              )}
              style={
                entry.endpointId === ""
                  ? undefined
                  : { background: ACTIVITY_COLORS[entry.kind] }
              }
            />
            {entry.endpointId === "" ? (
              // Synthetic line (e.g. pause-buffer overflow) — no endpoint to
              // link, so render the hook's message verbatim.
              <span className="min-w-0 truncate italic text-muted-foreground">
                {entry.message}
              </span>
            ) : (
              <span className="min-w-0 truncate">
                <Link
                  to={`/Endpoints/Details/${encodeURIComponent(entry.endpointId)}`}
                  className="text-foreground font-medium no-underline hover:text-primary"
                >
                  {entry.endpointId}
                </Link>
                <span className="text-muted-foreground">
                  {`: ${entry.count.toLocaleString()} ${FLOW_KIND_VERB[entry.kind]}`}
                </span>
              </span>
            )}
          </li>
        ))}
      </ul>
    )}
  </div>
);

// ---------------------------------------------------------------------------
// Small helpers
// ---------------------------------------------------------------------------

function formatSpeed(speed: number): string {
  return speed
    .toFixed(2)
    .replace(/0+$/, "")
    .replace(/\.$/, "");
}

function truncate(s: string, n: number): string {
  return s.length > n ? `${s.slice(0, n - 1)}…` : s;
}

function relativeAgo(ms: number): string {
  const s = Math.max(0, Math.round(ms / 1000));
  if (s < 60) return `${s}s`;
  const m = Math.round(s / 60);
  if (m < 60) return `${m}m`;
  const h = Math.round(m / 60);
  if (h < 24) return `${h}h`;
  return `${Math.round(h / 24)}d`;
}
