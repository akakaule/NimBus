import { useEffect, useMemo, useRef, useState } from "react";
import * as api from "api-client";
import Page from "components/page";
import { Button } from "components/ui/button";
import { Select } from "components/ui/select";
import { StatRow, StatTile } from "components/ui/stat-tile";
import {
  FilterToolbar,
  FilterSearch,
  FilterChip,
} from "components/ui/filter-toolbar";
import { EmptyState } from "components/ui/empty-state";
import { Spinner } from "components/ui/spinner";
import { cn } from "lib/utils";
import { TopologyGraph } from "components/topology/topology-graph";
import { TopologyInspector } from "components/topology/topology-inspector";
import { useTopologyData } from "components/topology/use-topology-data";
import { useUrlFilters } from "hooks/use-url-filters";

const PERIODS: Array<{ label: string; value: api.Period }> = [
  { label: "1h", value: api.Period._1h },
  { label: "12h", value: api.Period._12h },
  { label: "1d", value: api.Period._1d },
  { label: "7d", value: api.Period._7d },
];

// URL-driven filter state — survives Back from the node-detail click and keeps
// the topology view shareable via copy/paste.
type TopologyFilter = {
  searchTerm: string;
  selectedNamespace: string;
  selectedEndpoint: string;
  showOnlyTraffic: string; // "1" when active, "" otherwise
};

const DEFAULT_TOPOLOGY_FILTER: TopologyFilter = {
  searchTerm: "",
  selectedNamespace: "",
  selectedEndpoint: "",
  showOnlyTraffic: "",
};

export default function Topology() {
  const { applied, setFiltersWithoutHistory } =
    useUrlFilters<TopologyFilter>(DEFAULT_TOPOLOGY_FILTER);

  const search = applied.searchTerm;
  const namespace = applied.selectedNamespace || undefined;
  const endpoint = applied.selectedEndpoint || undefined;
  const hideIdleEdges = applied.showOnlyTraffic === "1";

  const [period, setPeriod] = useState<api.Period>(api.Period._1h);
  const [selectedNodeId, setSelectedNodeId] = useState<string | undefined>();

  const { data, loading, refresh, lastUpdated, error, allNamespaces, allEndpoints } =
    useTopologyData({
      period,
      namespace,
      endpoint,
      hideIdleEdges,
    });

  // Live filter setters — replaceState so per-keystroke history isn't polluted.
  const setSearch = (next: string) =>
    setFiltersWithoutHistory({ ...applied, searchTerm: next });
  const setNamespace = (next: string) =>
    setFiltersWithoutHistory({ ...applied, selectedNamespace: next });
  const setEndpoint = (next: string) =>
    setFiltersWithoutHistory({ ...applied, selectedEndpoint: next });
  const setShowOnlyTraffic = (next: boolean) =>
    setFiltersWithoutHistory({
      ...applied,
      showOnlyTraffic: next ? "1" : "",
    });


  // Highlight filter — dim non-matching nodes via auto-select of the first
  // match. Same behaviour as before; just sourced from URL-driven `search`.
  const filteredData = useMemo(() => {
    if (!data) return data;
    if (!search.trim()) return data;
    const lower = search.toLowerCase();
    const matchingNodes = new Set(
      data.nodes
        .filter((n) => n.name.toLowerCase().includes(lower))
        .map((n) => n.id),
    );
    if (matchingNodes.size > 0 && !selectedNodeId) {
      const first = Array.from(matchingNodes)[0];
      Promise.resolve().then(() => setSelectedNodeId(first));
    }
    return data;
  }, [data, search, selectedNodeId]);

  const periodLabel =
    PERIODS.find((p) => p.value === period)?.label ?? "1h";

  return (
    <Page
      title="Topology"
      subtitle="Live view of every endpoint, the event types they publish, and which consumers subscribe."
      actions={
        <>
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
          <Button
            variant="ghost"
            size="sm"
            onClick={() => exportSvg()}
            title="Download current graph as SVG"
          >
            ⤓ Export SVG
          </Button>
          <Button
            variant="solid"
            size="sm"
            onClick={() => void refresh()}
            isLoading={loading}
          >
            ↻ Refresh
          </Button>
        </>
      }
    >
      <div className="w-full flex flex-col gap-4">
        <FilterToolbar
          search={
            <FilterSearch
              value={search}
              onChange={setSearch}
              placeholder="Highlight endpoints or event types…"
            />
          }
          chips={
            <>
              {namespace && (
                <FilterChip
                  field="Namespace"
                  value={namespace}
                  onRemove={() => setNamespace("")}
                />
              )}
              {endpoint && (
                <FilterChip
                  field="Endpoint"
                  value={endpoint}
                  onRemove={() => setEndpoint("")}
                />
              )}
              {hideIdleEdges && (
                <FilterChip
                  field="Show"
                  value="edges with traffic"
                  onRemove={() => setShowOnlyTraffic(false)}
                />
              )}
            </>
          }
          actions={
            <AddFilterMenu
              namespaces={allNamespaces}
              endpoints={allEndpoints}
              hasNamespaceFilter={!!namespace}
              hasEndpointFilter={!!endpoint}
              hasTrafficFilter={hideIdleEdges}
              onPickNamespace={setNamespace}
              onPickEndpoint={setEndpoint}
              onEnableTrafficFilter={() => setShowOnlyTraffic(true)}
            />
          }
        />

        {data ? (
          <>
            <StatRow columns={4}>
              <StatTile
                label="Endpoints"
                value={data.summary.endpoints.toLocaleString()}
                delta={`${data.summary.producingEndpoints} producing · ${data.summary.consumingEndpoints} consuming`}
                tone="muted"
              />
              <StatTile
                label="Event types"
                value={data.summary.eventTypes.toLocaleString()}
                delta={`${data.summary.namespaces} namespace${data.summary.namespaces === 1 ? "" : "s"}`}
                tone="muted"
              />
              <StatTile
                label="Pub-sub edges"
                value={data.summary.edges.toLocaleString()}
                delta={`window · ${periodLabel}`}
                tone="muted"
              />
              <StatTile
                label="Edges with failures"
                value={data.summary.edgesWithFailures.toLocaleString()}
                delta={
                  data.summary.edgesWithFailures > 0
                    ? "needs attention"
                    : "all clear"
                }
                tone={data.summary.edgesWithFailures > 0 ? "danger" : "default"}
              />
            </StatRow>

            <Legend />

            <div className="grid grid-cols-1 lg:grid-cols-[1fr_320px] gap-4">
              <TopologyGraph
                nodes={filteredData?.nodes ?? data.nodes}
                edges={data.edges}
                pills={data.pills}
                selectedNodeId={selectedNodeId}
                onSelectNode={setSelectedNodeId}
              />
              <TopologyInspector
                data={data}
                selectedNodeId={selectedNodeId}
                lastUpdated={lastUpdated}
                periodLabel={periodLabel}
              />
            </div>
          </>
        ) : loading ? (
          <div className="flex items-center justify-center h-[400px] w-full">
            <Spinner size="xl" color="primary" />
          </div>
        ) : (
          <EmptyState
            icon="◌"
            title={error ?? "No topology yet"}
            description="Register an event type with at least one producer or consumer to see the graph."
          />
        )}
      </div>
    </Page>
  );
}

interface AddFilterMenuProps {
  namespaces: string[];
  endpoints: string[];
  hasNamespaceFilter: boolean;
  hasEndpointFilter: boolean;
  hasTrafficFilter: boolean;
  onPickNamespace: (value: string) => void;
  onPickEndpoint: (value: string) => void;
  onEnableTrafficFilter: () => void;
}

type MenuMode = "closed" | "list" | "pick-namespace" | "pick-endpoint";

// Lightweight popover so the disabled "+ Add filter" stub becomes a real
// dimension picker. Only inactive dimensions appear in the list — picking one
// applies the filter, closes the menu, and renders a removable chip via the
// parent's FilterToolbar.
const AddFilterMenu: React.FC<AddFilterMenuProps> = ({
  namespaces,
  endpoints,
  hasNamespaceFilter,
  hasEndpointFilter,
  hasTrafficFilter,
  onPickNamespace,
  onPickEndpoint,
  onEnableTrafficFilter,
}) => {
  const [mode, setMode] = useState<MenuMode>("closed");
  const containerRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (mode === "closed") return;
    const handleClick = (event: MouseEvent) => {
      if (containerRef.current && !containerRef.current.contains(event.target as Node)) {
        setMode("closed");
      }
    };
    document.addEventListener("mousedown", handleClick);
    return () => document.removeEventListener("mousedown", handleClick);
  }, [mode]);

  const allDimensionsActive =
    hasNamespaceFilter && hasEndpointFilter && hasTrafficFilter;

  return (
    <div ref={containerRef} className="relative">
      <Button
        variant="ghost"
        size="sm"
        onClick={() => setMode(mode === "closed" ? "list" : "closed")}
        disabled={allDimensionsActive}
        title={
          allDimensionsActive
            ? "All filters applied"
            : "Add a filter to narrow the graph"
        }
      >
        + Add filter
      </Button>

      {mode !== "closed" && (
        <div
          className={cn(
            "absolute z-10 mt-1 right-0",
            "bg-card border border-border rounded-nb-md shadow-md",
            "min-w-[260px] p-2",
          )}
          role="menu"
        >
          {mode === "list" && (
            <div className="flex flex-col gap-1">
              {!hasNamespaceFilter && (
                <MenuRow onClick={() => setMode("pick-namespace")}>
                  Namespace…
                </MenuRow>
              )}
              {!hasEndpointFilter && (
                <MenuRow onClick={() => setMode("pick-endpoint")}>
                  Endpoint…
                </MenuRow>
              )}
              {!hasTrafficFilter && (
                <MenuRow
                  onClick={() => {
                    onEnableTrafficFilter();
                    setMode("closed");
                  }}
                >
                  Show: edges with traffic
                </MenuRow>
              )}
            </div>
          )}

          {mode === "pick-namespace" && (
            <PickerPanel
              label="Pick a namespace"
              options={namespaces}
              emptyHint="No namespaces available."
              onPick={(value) => {
                onPickNamespace(value);
                setMode("closed");
              }}
              onCancel={() => setMode("list")}
            />
          )}

          {mode === "pick-endpoint" && (
            <PickerPanel
              label="Pick an endpoint"
              options={endpoints}
              emptyHint="No endpoints available."
              onPick={(value) => {
                onPickEndpoint(value);
                setMode("closed");
              }}
              onCancel={() => setMode("list")}
            />
          )}
        </div>
      )}
    </div>
  );
};

const MenuRow: React.FC<{
  onClick: () => void;
  children: React.ReactNode;
}> = ({ onClick, children }) => (
  <button
    type="button"
    onClick={onClick}
    role="menuitem"
    className={cn(
      "text-left text-[13px] px-2.5 py-1.5 rounded-nb-sm",
      "hover:bg-muted/60 transition-colors",
    )}
  >
    {children}
  </button>
);

const PickerPanel: React.FC<{
  label: string;
  options: string[];
  emptyHint: string;
  onPick: (value: string) => void;
  onCancel: () => void;
}> = ({ label, options, emptyHint, onPick, onCancel }) => (
  <div className="flex flex-col gap-2">
    <div className="flex items-center justify-between text-[11.5px] font-mono uppercase text-muted-foreground px-1">
      <span>{label}</span>
      <button
        type="button"
        onClick={onCancel}
        className="hover:text-foreground"
        aria-label="Back"
      >
        ←
      </button>
    </div>
    {options.length === 0 ? (
      <p className="text-[12px] text-muted-foreground px-1">{emptyHint}</p>
    ) : (
      <Select
        autoFocus
        defaultValue=""
        onChange={(e) => {
          if (e.target.value) onPick(e.target.value);
        }}
      >
        <option value="" disabled>
          Choose…
        </option>
        {options.map((opt) => (
          <option key={opt} value={opt}>
            {opt}
          </option>
        ))}
      </Select>
    )}
  </div>
);

const Legend: React.FC = () => (
  <div className="flex gap-5 flex-wrap items-center font-mono text-[11px] text-muted-foreground">
    <LegendNode color="var(--nb-success,#2E8F5E)" tint="var(--nb-success-50,#DCEFE4)">
      Healthy endpoint
    </LegendNode>
    <LegendNode color="var(--nb-warning,#C98A1B)" tint="var(--nb-warning-50,#F6E7C7)">
      Deferred / above P95
    </LegendNode>
    <LegendNode color="var(--nb-danger,#C2412E)" tint="var(--nb-danger-50,#F4D9D3)">
      Failures present
    </LegendNode>
    <LegendNode color="var(--nb-border-strong,#C9C1AB)" tint="var(--nb-surface-2,#ECE7DA)">
      Idle (no traffic)
    </LegendNode>
    <span aria-hidden="true" className="w-px h-3.5 bg-border" />
    <LegendLine color="var(--nb-success,#2E8F5E)">Publishes</LegendLine>
    <LegendLine color="var(--nb-info,#3A6FB0)">Subscribes</LegendLine>
    <LegendLine color="var(--nb-danger,#C2412E)">Failing edge</LegendLine>
    <LegendLine color="var(--nb-ink-3,#8A8473)" dashed>
      Idle / no traffic
    </LegendLine>
    <span className="ml-auto italic text-muted-foreground">
      Click any endpoint to focus its sub-graph.
    </span>
  </div>
);

const LegendNode: React.FC<{
  color: string;
  tint: string;
  children: React.ReactNode;
}> = ({ color, tint, children }) => (
  <span className="inline-flex items-center gap-1.5">
    <span
      aria-hidden="true"
      className="inline-block w-3.5 h-2.5 rounded-[3px] border-[1.5px]"
      style={{ borderColor: color, background: tint }}
    />
    {children}
  </span>
);

const LegendLine: React.FC<{
  color: string;
  dashed?: boolean;
  children: React.ReactNode;
}> = ({ color, dashed, children }) => (
  <span className="inline-flex items-center gap-1.5">
    <span
      aria-hidden="true"
      className="inline-block w-4 h-0 align-middle"
      style={{
        borderTopWidth: 2,
        borderTopStyle: dashed ? "dashed" : "solid",
        borderTopColor: color,
      }}
    />
    {children}
  </span>
);

function exportSvg(): void {
  const svg = document.querySelector<SVGSVGElement>(".rounded-nb-lg svg");
  if (!svg) return;
  const serializer = new XMLSerializer();
  const xml = `<?xml version="1.0" standalone="no"?>\r\n${serializer.serializeToString(svg)}`;
  const blob = new Blob([xml], { type: "image/svg+xml;charset=utf-8" });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = "nimbus-topology.svg";
  a.click();
  URL.revokeObjectURL(url);
}
