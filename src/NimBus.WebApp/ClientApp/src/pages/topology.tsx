import { useMemo, useState } from "react";
import * as api from "api-client";
import Page from "components/page";
import { Button } from "components/ui/button";
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

const PERIODS: Array<{ label: string; value: api.Period }> = [
  { label: "1h", value: api.Period._1h },
  { label: "12h", value: api.Period._12h },
  { label: "1d", value: api.Period._1d },
  { label: "7d", value: api.Period._7d },
];

export default function Topology() {
  const [period, setPeriod] = useState<api.Period>(api.Period._1h);
  const [search, setSearch] = useState("");
  const [namespace, setNamespace] = useState<string | undefined>(undefined);
  const [selectedNodeId, setSelectedNodeId] = useState<string | undefined>();
  const { data, loading, refresh, lastUpdated, error } = useTopologyData({
    period,
    namespace,
  });

  // Highlight filter — when the user types a search term, dim every node that
  // doesn't match. We don't filter edges away because hiding context confuses
  // the graph shape; matching nodes get an orange ring via selectedNodeId
  // pass-through, all others keep their normal styling. (Selection still wins.)
  const filteredData = useMemo(() => {
    if (!data) return data;
    if (!search.trim()) return data;
    const lower = search.toLowerCase();
    const matchingNodes = new Set(
      data.nodes
        .filter((n) => n.name.toLowerCase().includes(lower))
        .map((n) => n.id),
    );
    // Surface a match by auto-selecting the first one — operators searching
    // by name expect their result to be highlighted.
    if (matchingNodes.size > 0 && !selectedNodeId) {
      const first = Array.from(matchingNodes)[0];
      // Defer to avoid setting state during render
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
                  onRemove={() => setNamespace(undefined)}
                />
              )}
            </>
          }
          actions={
            <Button variant="ghost" size="sm" disabled title="Filter builder — coming soon">
              + Add filter
            </Button>
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
  // Find the first topology SVG on the page and download it as a file. We
  // intentionally serialise the live DOM so the exported file matches what
  // the operator was looking at including selection / hover state.
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
