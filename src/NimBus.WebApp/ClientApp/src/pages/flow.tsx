import { useEffect, useMemo, useRef, useState } from "react";
import * as api from "api-client";
import Page from "components/page";
import { Button } from "components/ui/button";
import { Checkbox } from "components/ui/checkbox";
import { EmptyState } from "components/ui/empty-state";
import { Select } from "components/ui/select";
import { Spinner } from "components/ui/spinner";
import { cn } from "lib/utils";
import { topEndpointIds } from "components/flow/layout";
import { SpineView } from "components/flow/spine-view";
import { TOP_N_DEFAULT } from "components/flow/types";
import type { ConnectionMode } from "components/flow/types";
import type { TopologyNode } from "components/topology/types";
import { useFlowData } from "hooks/use-flow-data";
import { useUrlFilters } from "hooks/use-url-filters";

// Flow page — the event-type spine (design 1b: publisher → event type →
// subscriber). The spec-020 animated dots canvas was removed in favor of the
// lanes; useFlowData still runs the live snapshot pipeline so subscriber
// cards carry current failure counts and the mode badge stays honest.

const PERIODS: Array<{ label: string; value: api.Period }> = [
  { label: "1h", value: api.Period._1h },
  { label: "12h", value: api.Period._12h },
  { label: "1d", value: api.Period._1d },
  { label: "7d", value: api.Period._7d },
];

/** Window length in minutes per period — the lanes show per-minute rates. */
const PERIOD_MINUTES: Partial<Record<api.Period, number>> = {
  [api.Period._1h]: 60,
  [api.Period._12h]: 720,
  [api.Period._1d]: 1440,
  [api.Period._7d]: 10080,
};

/** The lanes need no animation events; the hook requires a callback. */
const NOOP_ACTIVITY = (): void => {};

// ---------------------------------------------------------------------------
// Persisted preferences (OQ-2 resolution: persist the endpoint filter)
// ---------------------------------------------------------------------------

const PREFS_KEY = "nb.flow.v1";

interface FlowPrefs {
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
  if (typeof window === "undefined") return {};
  try {
    const raw = window.localStorage.getItem(PREFS_KEY);
    if (!raw) return {};
    const parsed = JSON.parse(raw) as Partial<FlowPrefs> | null;
    if (!parsed || typeof parsed !== "object") return {};
    const endpointIds = Array.isArray(parsed.endpointIds)
      ? parsed.endpointIds.filter((id): id is string => typeof id === "string")
      : parsed.endpointIds === null
        ? null
        : undefined;
    return { endpointIds };
  } catch {
    return {};
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
  const [selection, setSelection] = useState<string[] | null | undefined>(
    () => loadPrefs().endpointIds,
  );
  // The event-type filter rides the URL (?eventType=…) so an incident view is
  // shareable by copy/paste — the same pattern as the Topology page's filters.
  const { applied: urlFilters, setFiltersWithoutHistory } = useUrlFilters<{
    eventType: string;
  }>({ eventType: "" });
  const eventType = urlFilters.eventType;
  const setEventType = (next: string): void =>
    setFiltersWithoutHistory({ eventType: next });

  const { topology, topologyLoading, topologyError, mode, snapshots } =
    useFlowData({ period, paused: false, onActivity: NOOP_ACTIVITY });

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

  const eventTypeOptions = useMemo(() => {
    if (topology === undefined) return [];
    const out = new Set<string>();
    for (const edge of topology.flowEdges) {
      for (const id of edge.eventTypeIds) out.add(id);
    }
    return Array.from(out).sort();
  }, [topology]);

  useEffect(() => {
    savePrefs({ endpointIds: selection });
  }, [selection]);

  const visibleCount = useMemo(() => {
    if (topology === undefined) return 0;
    if (effectiveSelection === null) return topology.nodes.length;
    const known = new Set(topology.nodes.map((n) => n.id));
    return effectiveSelection.filter((id) => known.has(id)).length;
  }, [topology, effectiveSelection]);

  return (
    <Page
      title="Flow"
      subtitle="Publisher → event type → subscriber — what is actually on the bus over the selected window."
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
        {topology !== undefined && topology.nodes.length > 0 ? (
          <>
            <div className="flex flex-wrap items-center gap-2.5">
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

            <SpineView
              topology={topology}
              visibleEndpointIds={visibleSet}
              eventType={eventType}
              periodMinutes={PERIOD_MINUTES[period] ?? 60}
              periodLabel={
                PERIODS.find((p) => p.value === period)?.label ?? ""
              }
              snapshots={snapshots}
            />
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
    const handleKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        setOpen(false);
      }
    };
    document.addEventListener("mousedown", handleClick);
    document.addEventListener("keydown", handleKey);
    return () => {
      document.removeEventListener("mousedown", handleClick);
      document.removeEventListener("keydown", handleKey);
    };
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
