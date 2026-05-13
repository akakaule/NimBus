import { Link } from "react-router-dom";
import { Badge } from "components/ui/badge";
import { cn } from "lib/utils";
import type { TopologyData, TopologyNode } from "./types";

interface TopologyInspectorProps {
  data: TopologyData;
  selectedNodeId?: string;
  lastUpdated?: Date;
  /** Display name of the selected period — e.g. "1h" — for KPI label suffixes. */
  periodLabel: string;
  className?: string;
}

/**
 * Sticky right rail surfacing details on the selected endpoint:
 * - mini KPI grid (handled, failed, latency placeholder, backlog placeholder)
 * - lists of published / subscribed event types (deep-linkable)
 * - downstream endpoints with failure callouts
 * - "Open endpoint ›" footer linking into the full Endpoint Details page
 *
 * When no endpoint is selected, a short prompt nudges the operator to click
 * one — keeps the rail useful instead of blank.
 */
export const TopologyInspector: React.FC<TopologyInspectorProps> = ({
  data,
  selectedNodeId,
  lastUpdated,
  periodLabel,
  className,
}) => {
  const node = selectedNodeId
    ? data.nodes.find((n) => n.id === selectedNodeId)
    : undefined;

  if (!node) {
    return (
      <aside
        className={cn(
          "bg-card border border-border rounded-nb-lg p-5",
          "self-start sticky top-20",
          className,
        )}
      >
        <p className="font-mono text-[11px] uppercase tracking-[0.12em] text-muted-foreground m-0 mb-2">
          Inspector
        </p>
        <p className="text-sm text-muted-foreground italic m-0">
          Click any endpoint to focus its sub-graph.
        </p>
      </aside>
    );
  }

  // Distinct event types this endpoint publishes / subscribes — derived from
  // the edges that include the selected node.
  const publishEdge = data.edges.find(
    (e) => e.kind === "publish" && e.endpointId === node.id,
  );
  const subscribeEdge = data.edges.find(
    (e) => e.kind === "subscribe" && e.endpointId === node.id,
  );

  // Downstream — endpoints whose subscribe edges reference any event type this
  // node publishes. (Approximation: we don't track per-pair on the client.)
  const publishedTypeIds = new Set(publishEdge?.eventTypeIds ?? []);
  const downstream = data.nodes.filter((other) => {
    if (other.id === node.id) return false;
    const sub = data.edges.find(
      (e) => e.kind === "subscribe" && e.endpointId === other.id,
    );
    if (!sub) return false;
    return sub.eventTypeIds.some((t) => publishedTypeIds.has(t));
  });

  return (
    <aside
      className={cn(
        "bg-card border border-border rounded-nb-lg p-5",
        "flex flex-col gap-4 self-start sticky top-20",
        "max-h-[calc(100vh-100px)] overflow-auto",
        className,
      )}
    >
      <div>
        <p className="font-mono text-[11px] uppercase tracking-[0.12em] text-muted-foreground m-0 mb-1">
          Selected endpoint
        </p>
        <h3 className="m-0 text-base font-bold tracking-tight">{node.name}</h3>
        <p className="text-[12.5px] text-muted-foreground m-0 mt-1">
          Owned by <b className="text-foreground">{node.role}</b>
        </p>
      </div>

      {/* 2×2 KPI grid. Latency & backlog stay placeholders until the API exposes
          them — better to show a dash than a fake number. */}
      <div className="grid grid-cols-2 gap-1.5">
        <MiniKpi
          label={`Handled · ${periodLabel}`}
          value={node.handledMessages.toLocaleString()}
          tone="ok"
        />
        <MiniKpi
          label={`Failed · ${periodLabel}`}
          value={node.failedMessages.toLocaleString()}
          tone={node.failedMessages > 0 ? "bad" : "muted"}
        />
        <MiniKpi label="Avg latency" value="—" suffix=" ms" tone="muted" />
        <MiniKpi label="Backlog" value="—" tone="muted" />
      </div>

      <EventList
        title="Publishes"
        accent="success"
        count={publishEdge?.eventTypeIds.length ?? 0}
        eventTypeIds={publishEdge?.eventTypeIds ?? []}
      />

      <EventList
        title="Subscribes"
        accent="info"
        count={subscribeEdge?.eventTypeIds.length ?? 0}
        eventTypeIds={subscribeEdge?.eventTypeIds ?? []}
      />

      {downstream.length > 0 && (
        <div>
          <SectionTitle>Downstream</SectionTitle>
          <div className="flex flex-col gap-1">
            {downstream.map((d) => (
              <DownstreamRow key={d.id} node={d} />
            ))}
          </div>
        </div>
      )}

      <div className="flex justify-between items-center pt-2 border-t border-border font-mono text-[11px] text-muted-foreground">
        <span>{formatUpdatedAgo(lastUpdated)}</span>
        <Link
          to={`/Endpoints/Details/${node.id}`}
          className="text-primary-600 hover:text-primary font-semibold no-underline"
        >
          Open endpoint ›
        </Link>
      </div>
    </aside>
  );
};

const SectionTitle: React.FC<{ children: React.ReactNode }> = ({ children }) => (
  <p className="font-mono text-[11px] uppercase tracking-[0.12em] text-muted-foreground m-0 mb-2">
    {children}
  </p>
);

interface MiniKpiProps {
  label: string;
  value: string;
  suffix?: string;
  tone: "ok" | "warn" | "bad" | "muted";
}

const MiniKpi: React.FC<MiniKpiProps> = ({ label, value, suffix, tone }) => {
  const toneClass = {
    ok: "text-status-success",
    warn: "text-status-warning",
    bad: "text-status-danger",
    muted: "text-foreground",
  }[tone];
  return (
    <div className="bg-background border border-border rounded-md px-2.5 py-2">
      <div className="font-mono text-[9.5px] uppercase tracking-[0.12em] text-muted-foreground mb-1">
        {label}
      </div>
      <div className={cn("text-base font-bold leading-tight tabular-nums", toneClass)}>
        {value}
        {suffix && (
          <small className="text-[11px] font-medium text-muted-foreground ml-0.5">
            {suffix}
          </small>
        )}
      </div>
    </div>
  );
};

interface EventListProps {
  title: string;
  accent: "success" | "info";
  count: number;
  eventTypeIds: string[];
}

const EventList: React.FC<EventListProps> = ({
  title,
  accent,
  count,
  eventTypeIds,
}) => {
  const accentColor = accent === "success" ? "text-status-success" : "text-status-info";
  const dotColor = accent === "success" ? "bg-status-success" : "bg-status-info";
  return (
    <div>
      <SectionTitle>
        {title}{" "}
        <span className={cn("font-mono font-semibold ml-1", accentColor)}>
          {count}
        </span>
      </SectionTitle>
      {eventTypeIds.length === 0 ? (
        <p className="text-[12px] text-muted-foreground italic m-0">none</p>
      ) : (
        <div className="flex flex-col gap-1">
          {eventTypeIds.map((id) => (
            <Link
              key={id}
              to={`/EventTypes/Details/${id}`}
              className={cn(
                "flex items-center gap-2 px-2 py-1.5 rounded-md no-underline",
                "bg-background border border-border text-foreground text-[12.5px]",
                "hover:border-border-strong",
              )}
            >
              <span
                aria-hidden="true"
                className={cn("w-1.5 h-1.5 rounded-full shrink-0", dotColor)}
              />
              <span className="font-semibold flex-1 truncate">{id}</span>
            </Link>
          ))}
        </div>
      )}
    </div>
  );
};

interface DownstreamRowProps {
  node: TopologyNode;
}

const DownstreamRow: React.FC<DownstreamRowProps> = ({ node }) => {
  const isFailing = node.failedMessages > 0;
  return (
    <Link
      to={`/Endpoints/Details/${node.id}`}
      className={cn(
        "flex items-center gap-2 px-2 py-1.5 rounded-md no-underline text-[12.5px]",
        isFailing
          ? "bg-status-danger-50 border border-status-danger-50 text-status-danger-ink"
          : "bg-background border border-border text-foreground hover:border-border-strong",
      )}
    >
      <span
        aria-hidden="true"
        className={cn(
          "w-1.5 h-1.5 rounded-full shrink-0",
          isFailing ? "bg-status-danger" : "bg-status-success",
        )}
      />
      <span className="font-semibold flex-1 truncate">{node.name}</span>
      {isFailing && (
        <Badge variant="failed" size="sm">
          {node.failedMessages} fail
        </Badge>
      )}
    </Link>
  );
};

function formatUpdatedAgo(d: Date | undefined): string {
  if (!d) return "—";
  const seconds = Math.max(0, Math.floor((Date.now() - d.getTime()) / 1000));
  if (seconds < 60) return `Updated ${seconds}s ago`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `Updated ${minutes}m ago`;
  return `Updated ${Math.floor(minutes / 60)}h ago`;
}
