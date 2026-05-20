import { useEffect, useMemo, useRef, useState } from "react";
import { cn } from "lib/utils";
import type { FlowEdge, TopologyNode } from "./types";

const VIEWBOX_W = 920;
const VIEWBOX_H = 580;
const NODE_W = 200;
const NODE_H = 60;
const PUB_X = 24;
const SUB_X = VIEWBOX_W - 24 - NODE_W;
const PUB_GAP = 16;
const SUB_GAP = 24;
const RIBBON_GAP = 2;
const AXIS_Y = 18;
const AXIS_LINE_Y = 26;
// Min / max stroke widths so a tiny ribbon doesn't vanish and a huge one
// doesn't crowd the whole rail. We linearly interpolate within these bounds
// from the loudest route in the dataset.
const MIN_RIBBON = 3;
const MAX_RIBBON = 14;
// Idle routes still need *some* thickness to be hoverable; pin them to the floor.
const IDLE_RIBBON = 3;

interface TopologyFlowProps {
  nodes: TopologyNode[];
  flowEdges: FlowEdge[];
  selectedNodeId?: string;
  onSelectNode: (id: string | undefined) => void;
  className?: string;
}

interface FlowSide {
  id: string;
  name: string;
  team: string;
  role: string;
  metric: string;
  metricKind: "ok" | "bad" | "warn" | "idle";
  health: "good" | "warn" | "bad" | "idle";
}

interface LaidOutSide extends FlowSide {
  y: number;
  mid: number;
}

interface LaidOutEdge extends FlowEdge {
  srcY: number;
  tgtY: number;
  strokeWidth: number;
}

/**
 * Bipartite (Sankey-style) view of the topology. Publishers stack on the
 * left, subscribers on the right, with curved ribbons going *directly*
 * between them — no central bus in the way. Ribbon thickness scales with
 * traffic; colour is keyed to health (green ok / amber deferred / red
 * failing / muted-grey idle).
 *
 * Hovering a ribbon reveals a tooltip with the event types it carries.
 * Clicking a node mutes everything else and highlights that endpoint's
 * routes (an operator's "show me who I talk to" gesture).
 */
export const TopologyFlow: React.FC<TopologyFlowProps> = ({
  nodes,
  flowEdges,
  selectedNodeId,
  onSelectNode,
  className,
}) => {
  const wrapRef = useRef<HTMLDivElement | null>(null);
  const svgRef = useRef<SVGSVGElement | null>(null);
  const tagRef = useRef<HTMLSpanElement | null>(null);

  // Endpoint → node lookup for fast metric / health resolution. The node
  // catalogue is small (≤ a few dozen in practice) so we rebuild this each
  // render rather than memoising — the saved closures aren't worth the
  // dependency book-keeping.
  const nodeById = useMemo(() => {
    const map = new Map<string, TopologyNode>();
    for (const n of nodes) map.set(n.id, n);
    return map;
  }, [nodes]);

  const { publishers, subscribers, edges, hasData, maxMessages } = useMemo(
    () => buildLayout(nodes, nodeById, flowEdges),
    [nodes, nodeById, flowEdges],
  );

  // Tooltip plumbing — mirrors the graph view. One floating <span> outside
  // the SVG; on mousemove anywhere inside the canvas we read the closest
  // [data-tag] and reposition. Cleared on mouseleave.
  useEffect(() => {
    const wrap = wrapRef.current;
    const tag = tagRef.current;
    if (!wrap || !tag) return;
    const handleMove = (e: MouseEvent) => {
      const target = (e.target as HTMLElement | null)?.closest?.("[data-tag]");
      if (!target) {
        tag.classList.remove("show");
        return;
      }
      const text = target.getAttribute("data-tag") ?? "";
      tag.textContent = text;
      const r = wrap.getBoundingClientRect();
      tag.style.left = `${e.clientX - r.left}px`;
      tag.style.top = `${e.clientY - r.top}px`;
      tag.classList.add("show");
    };
    const handleLeave = () => tag.classList.remove("show");
    wrap.addEventListener("mousemove", handleMove);
    wrap.addEventListener("mouseleave", handleLeave);
    return () => {
      wrap.removeEventListener("mousemove", handleMove);
      wrap.removeEventListener("mouseleave", handleLeave);
    };
  }, []);

  // Tracks which node is "focused" — initialised from the parent-supplied
  // `selectedNodeId` (so deep-linking still works), but clicking a node
  // toggles between focus + global view without round-tripping the parent.
  // Selecting via the canvas keeps inspector state in sync via onSelectNode.
  const [focusId, setFocusId] = useState<string | undefined>(selectedNodeId);
  useEffect(() => setFocusId(selectedNodeId), [selectedNodeId]);

  const handleNodeClick = (id: string) => {
    if (focusId === id) {
      setFocusId(undefined);
      return;
    }
    setFocusId(id);
    onSelectNode(id);
  };

  const muted = focusId !== undefined;
  // For each edge / node, work out whether it's part of the focused subgraph.
  // Highlighting takes the focused id + the immediate counterparties (every
  // node it sends to / receives from) so the operator gets a one-hop view.
  const highlightedNodeIds = useMemo(() => {
    if (!focusId) return undefined;
    const set = new Set<string>([focusId]);
    for (const e of edges) {
      if (e.from === focusId) set.add(e.to);
      if (e.to === focusId) set.add(e.from);
    }
    return set;
  }, [focusId, edges]);

  const isEdgeHighlighted = (e: LaidOutEdge) =>
    focusId !== undefined && (e.from === focusId || e.to === focusId);

  return (
    <div
      ref={wrapRef}
      className={cn(
        "relative bg-card border border-border rounded-nb-lg p-4 sm:p-5",
        className,
      )}
    >
      <FlowHeader />
      <div
        className={cn(
          "relative bg-canvas dark:bg-background border border-border rounded-nb-md overflow-hidden",
          // Dotted grid mirrors the graph view
          "bg-[radial-gradient(circle,#E5DFCE_1px,transparent_1px)] bg-[length:24px_24px]",
          "dark:bg-[radial-gradient(circle,#2A2620_1px,transparent_1px)]",
        )}
      >
        <span
          ref={tagRef}
          className={cn(
            "absolute pointer-events-none opacity-0 transition-opacity",
            "bg-ink text-canvas font-mono text-[11px] px-2 py-1 rounded-md",
            "shadow-nb-md whitespace-nowrap z-10",
            "-translate-x-1/2 -translate-y-[130%]",
            "[&.show]:opacity-100",
          )}
        />
        {hasData ? (
          <svg
            ref={svgRef}
            viewBox={`0 0 ${VIEWBOX_W} ${VIEWBOX_H}`}
            preserveAspectRatio="xMidYMid meet"
            className="block w-full h-auto"
            onClick={(e) => {
              // Clicking empty canvas clears focus. Children stop-propagate.
              if (e.target === e.currentTarget) {
                setFocusId(undefined);
              }
            }}
          >
            <FlowAxis />
            <FlowRibbons
              edges={edges}
              muted={muted}
              isHighlighted={isEdgeHighlighted}
              maxMessages={maxMessages}
            />
            <FlowNodes
              side="pub"
              entries={publishers}
              muted={muted}
              highlightedIds={highlightedNodeIds}
              onSelect={handleNodeClick}
              focusedId={focusId}
            />
            <FlowNodes
              side="sub"
              entries={subscribers}
              muted={muted}
              highlightedIds={highlightedNodeIds}
              onSelect={handleNodeClick}
              focusedId={focusId}
            />
          </svg>
        ) : (
          <div className="px-6 py-16 text-center text-sm text-muted-foreground">
            No producer → consumer routes match the current filters.
          </div>
        )}
      </div>
      <FlowFooter edges={flowEdges} publishers={publishers} subscribers={subscribers} />
    </div>
  );
};

// ----- Header --------------------------------------------------------------

const FlowHeader: React.FC = () => (
  <div className="flex justify-between items-baseline mb-3.5 gap-6 flex-wrap">
    <h3 className="text-[15px] font-bold m-0">Publisher → Subscriber flow</h3>
    <div className="font-mono text-[11px] text-muted-foreground flex gap-4 flex-wrap items-center">
      <span>
        <b className="text-ink-2 dark:text-foreground font-semibold">Left:</b>{" "}
        endpoints that publish
      </span>
      <span>
        <b className="text-ink-2 dark:text-foreground font-semibold">Right:</b>{" "}
        endpoints that receive
      </span>
      <span>
        <b className="text-ink-2 dark:text-foreground font-semibold">Ribbon width</b>{" "}
        ∝ traffic
      </span>
      <SwatchKey color="var(--nb-success,#2E8F5E)" label="healthy" />
      <SwatchKey color="var(--nb-danger,#C2412E)" label="failing" />
      <SwatchKey color="var(--nb-warning,#C98A1B)" label="deferred" />
      <SwatchKey color="#8A8473" label="idle" muted />
    </div>
  </div>
);

const SwatchKey: React.FC<{ color: string; label: string; muted?: boolean }> = ({
  color,
  label,
  muted,
}) => (
  <span className="inline-flex items-center">
    <span
      aria-hidden="true"
      className="inline-block w-3.5 h-1.5 rounded-[2px] mr-1.5 align-middle"
      style={{ background: color, opacity: muted ? 0.5 : 1 }}
    />
    {label}
  </span>
);

// ----- Axis ---------------------------------------------------------------

const FlowAxis: React.FC = () => (
  <g>
    <text
      x={PUB_X}
      y={AXIS_Y}
      className="font-mono"
      fill="var(--nb-ink-3, #8A8473)"
      style={{
        fontSize: 10.5,
        letterSpacing: "0.14em",
        fontWeight: 700,
        textTransform: "uppercase",
      }}
    >
      PUBLISHERS · sends events
    </text>
    <text
      x={SUB_X + NODE_W}
      y={AXIS_Y}
      textAnchor="end"
      className="font-mono"
      fill="var(--nb-ink-3, #8A8473)"
      style={{
        fontSize: 10.5,
        letterSpacing: "0.14em",
        fontWeight: 700,
        textTransform: "uppercase",
      }}
    >
      SUBSCRIBERS · receives events
    </text>
    <line
      x1={PUB_X}
      y1={AXIS_LINE_Y}
      x2={PUB_X + NODE_W}
      y2={AXIS_LINE_Y}
      stroke="var(--border-strong, #C9C1AB)"
      strokeWidth={1}
      strokeDasharray="2 4"
    />
    <line
      x1={SUB_X}
      y1={AXIS_LINE_Y}
      x2={SUB_X + NODE_W}
      y2={AXIS_LINE_Y}
      stroke="var(--border-strong, #C9C1AB)"
      strokeWidth={1}
      strokeDasharray="2 4"
    />
  </g>
);

// ----- Ribbons ------------------------------------------------------------

interface FlowRibbonsProps {
  edges: LaidOutEdge[];
  muted: boolean;
  isHighlighted: (e: LaidOutEdge) => boolean;
  maxMessages: number;
}

const FlowRibbons: React.FC<FlowRibbonsProps> = ({ edges, isHighlighted }) => (
  <g>
    {edges.map((e) => {
      const x1 = PUB_X + NODE_W;
      const x2 = SUB_X;
      const y1 = e.srcY;
      const y2 = e.tgtY;
      const cx1 = x1 + (x2 - x1) * 0.5;
      const cx2 = x2 - (x2 - x1) * 0.5;
      const d = `M ${x1} ${y1} C ${cx1} ${y1}, ${cx2} ${y2}, ${x2} ${y2}`;
      const stroke = strokeForHealth(e.health);
      const baseOpacity = opacityForHealth(e.health);
      const hl = isHighlighted(e);
      const isIdle = e.health === "idle";
      return (
        <g key={e.id}>
          {/* Solid base ribbon: gives the route its body so the moving
              dashes overlay reads as "flow on a real ribbon" rather than
              gaps showing the canvas through. Idle routes skip the base
              and stay statically dashed to communicate "no traffic". */}
          {!isIdle && (
            <path
              d={d}
              fill="none"
              stroke={stroke}
              strokeWidth={e.strokeWidth}
              strokeLinecap="butt"
              data-tag={e.tooltip}
              className={cn(
                "transition-[opacity,filter] duration-150 pointer-events-auto",
                hl && "drop-shadow-sm",
              )}
              style={{ opacity: hl ? 0.7 : baseOpacity * 0.75 }}
            />
          )}
          {/* Marching-dash overlay — reuses the `nb-flow` keyframe from the
              graph view so the animation cadence stays consistent across
              both topology renderings. For idle this is the sole ribbon
              (static dashes, no animation). */}
          <path
            d={d}
            fill="none"
            stroke={stroke}
            strokeWidth={e.strokeWidth}
            strokeLinecap="butt"
            data-tag={e.tooltip}
            className={cn(
              "transition-[opacity,filter] duration-150 pointer-events-auto",
              !isIdle && "nb-flow-anim-ribbon",
              hl && "drop-shadow-sm",
            )}
            style={{
              opacity: isIdle ? baseOpacity : hl ? 0.95 : Math.min(1, baseOpacity + 0.25),
              strokeDasharray: isIdle ? "4 7" : undefined,
            }}
          />
          {/* Wider invisible hit area so hover doesn't require pixel-precision. */}
          <path
            d={d}
            fill="none"
            stroke="transparent"
            strokeWidth={Math.max(e.strokeWidth + 10, 16)}
            data-tag={e.tooltip}
            style={{ cursor: "pointer" }}
          />
        </g>
      );
    })}
  </g>
);

function strokeForHealth(h: FlowEdge["health"]): string {
  switch (h) {
    case "live":
      return "var(--nb-success,#2E8F5E)";
    case "warn":
      return "var(--nb-warning,#C98A1B)";
    case "fail":
      return "var(--nb-danger,#C2412E)";
    case "idle":
    default:
      return "#8A8473";
  }
}

function opacityForHealth(h: FlowEdge["health"]): number {
  switch (h) {
    case "live":
      return 0.5;
    case "warn":
      return 0.6;
    case "fail":
      return 0.62;
    case "idle":
    default:
      return 0.25;
  }
}

// ----- Nodes -------------------------------------------------------------

interface FlowNodesProps {
  side: "pub" | "sub";
  entries: LaidOutSide[];
  muted: boolean;
  highlightedIds: Set<string> | undefined;
  focusedId: string | undefined;
  onSelect: (id: string) => void;
}

const FlowNodes: React.FC<FlowNodesProps> = ({
  side,
  entries,
  muted,
  highlightedIds,
  focusedId,
  onSelect,
}) => {
  const x = side === "pub" ? PUB_X : SUB_X;
  return (
    <g>
      {entries.map((n) => {
        const hl = highlightedIds ? highlightedIds.has(n.id) : !muted;
        const dim = muted && !hl;
        const isFocused = focusedId === n.id;
        const tooltip = buildNodeTooltip(n, side);
        return (
          <g
            key={`${side}-${n.id}`}
            transform={`translate(${x}, ${n.y})`}
            style={{
              cursor: "pointer",
              opacity: dim ? 0.32 : 1,
              transition: "opacity .14s ease",
            }}
            data-tag={tooltip}
            onClick={(ev) => {
              ev.stopPropagation();
              onSelect(n.id);
            }}
          >
            <rect
              x={0}
              y={0}
              width={NODE_W}
              height={NODE_H}
              rx={8}
              fill="var(--card)"
              stroke={borderForHealth(n.health, isFocused)}
              strokeWidth={isFocused ? 2.4 : 1.4}
            />
            {/* Side strip — left edge for publishers, right edge for subscribers */}
            <rect
              x={side === "pub" ? 0 : NODE_W - 4}
              y={0}
              width={4}
              height={NODE_H}
              rx={2}
              fill={stripFillForHealth(n.health)}
            />
            {/* Role tag (PUB·n / SUB·n) on the strip side */}
            <text
              x={side === "pub" ? 14 : NODE_W - 12}
              y={16}
              textAnchor={side === "pub" ? "start" : "end"}
              className="font-mono uppercase"
              style={{
                fontSize: 9.5,
                letterSpacing: "0.06em",
                fontWeight: 600,
                fill: side === "pub" ? "var(--nb-success,#2E8F5E)" : "var(--nb-info,#3A6FB0)",
              }}
            >
              {side === "pub" ? `PUB · ${n.role}` : `SUB · ${n.role}`}
            </text>
            {/* Team label opposite side */}
            <text
              x={side === "pub" ? NODE_W - 12 : 14}
              y={16}
              textAnchor={side === "pub" ? "end" : "start"}
              className="font-mono"
              fill="var(--nb-ink-3, #8A8473)"
              style={{
                fontSize: 9,
                letterSpacing: "0.08em",
                textTransform: "uppercase",
              }}
            >
              {n.team}
            </text>
            <text
              x={12}
              y={38}
              className="font-sans"
              fill="var(--foreground, #1A1814)"
              style={{ fontSize: 13, fontWeight: 700 }}
            >
              {n.name}
            </text>
            <text
              x={12}
              y={54}
              className="font-mono"
              style={{
                fontSize: 10.5,
                fill: metricColor(n.metricKind),
                fontVariantNumeric: "tabular-nums",
              }}
            >
              {n.metric}
            </text>
          </g>
        );
      })}
    </g>
  );
};

function borderForHealth(h: FlowSide["health"], focused: boolean): string {
  if (focused) return "var(--nb-primary,#E8743C)";
  switch (h) {
    case "good":
      return "#A9D5BC";
    case "warn":
      return "#E0BD6B";
    case "bad":
      return "#D89A8E";
    case "idle":
    default:
      return "var(--border-strong)";
  }
}

function stripFillForHealth(h: FlowSide["health"]): string {
  switch (h) {
    case "good":
      return "var(--nb-success,#2E8F5E)";
    case "warn":
      return "var(--nb-warning,#C98A1B)";
    case "bad":
      return "var(--nb-danger,#C2412E)";
    case "idle":
    default:
      return "#8A8473";
  }
}

function metricColor(kind: FlowSide["metricKind"]): string {
  switch (kind) {
    case "ok":
      return "var(--foreground, #1A1814)";
    case "bad":
      return "var(--nb-danger,#C2412E)";
    case "warn":
      return "var(--nb-warning,#C98A1B)";
    case "idle":
    default:
      return "#8A8473";
  }
}

function buildNodeTooltip(n: LaidOutSide, side: "pub" | "sub"): string {
  return `${n.name} · ${side === "pub" ? "publisher" : "subscriber"} · ${n.metric}`;
}

// ----- Footer callouts ---------------------------------------------------

interface FlowFooterProps {
  edges: FlowEdge[];
  publishers: LaidOutSide[];
  subscribers: LaidOutSide[];
}

const FlowFooter: React.FC<FlowFooterProps> = ({ edges, publishers, subscribers }) => {
  // Derive a few quick observations the operator can read at a glance.
  const failing = edges.filter((e) => e.health === "fail");
  // Top-fan-in subscriber: who receives streams from the most publishers?
  const fanInBySub = new Map<string, number>();
  for (const e of edges) {
    fanInBySub.set(e.to, (fanInBySub.get(e.to) ?? 0) + 1);
  }
  const topFanIn = Array.from(fanInBySub.entries()).sort((a, b) => b[1] - a[1])[0];
  // Top-fan-out publisher: who sends to the most consumers?
  const fanOutByPub = new Map<string, number>();
  for (const e of edges) {
    fanOutByPub.set(e.from, (fanOutByPub.get(e.from) ?? 0) + 1);
  }
  const topFanOut = Array.from(fanOutByPub.entries()).sort((a, b) => b[1] - a[1])[0];

  // Skip the callouts entirely when there's nothing to summarise — keeps the
  // section quiet on empty / new installations rather than rendering hollow
  // boxes with "no data" copy.
  if (edges.length === 0) return null;

  return (
    <div className="mt-3 grid grid-cols-1 md:grid-cols-3 gap-2.5">
      {failing.length > 0 && (
        <CalloutCard tone="bad" icon="!" title={`${failing.length} failing route${failing.length === 1 ? "" : "s"}`}>
          {failing
            .slice(0, 2)
            .map((e) => `${e.from} → ${e.to} (${e.failures.toLocaleString()} failed)`)
            .join(" · ")}
          {failing.length > 2 ? ` · +${failing.length - 2} more` : ""}
        </CalloutCard>
      )}
      {topFanIn && topFanIn[1] >= 2 && (
        <CalloutCard tone="ok" icon="✓" title={`${nameFor(topFanIn[0], subscribers) ?? topFanIn[0]} receives from ${topFanIn[1]} publishers`}>
          Largest fan-in in this view. Hover any ribbon to see the exact event types in that route.
        </CalloutCard>
      )}
      {topFanOut && topFanOut[1] >= 2 && (
        <CalloutCard tone="warn" icon="↗" title={`${nameFor(topFanOut[0], publishers) ?? topFanOut[0]} fans out to ${topFanOut[1]} consumers`}>
          Click the publisher to mute everything else and focus only on its downstream routes.
        </CalloutCard>
      )}
    </div>
  );
};

function nameFor(id: string, list: LaidOutSide[]): string | undefined {
  return list.find((x) => x.id === id)?.name;
}

interface CalloutCardProps {
  tone: "bad" | "warn" | "ok";
  icon: string;
  title: string;
  children: React.ReactNode;
}

const CalloutCard: React.FC<CalloutCardProps> = ({ tone, icon, title, children }) => (
  <div className="bg-canvas dark:bg-background border border-border rounded-nb-md p-2.5 flex gap-2.5 items-start">
    <span
      className={cn(
        "w-[22px] h-[22px] rounded-[5px] inline-flex items-center justify-center font-mono text-[11px] font-bold flex-shrink-0",
        tone === "bad" && "bg-status-danger-50 text-status-danger-ink",
        tone === "warn" && "bg-status-warning-50 text-status-warning-ink",
        tone === "ok" && "bg-status-success-50 text-status-success-ink",
      )}
    >
      {icon}
    </span>
    <div>
      <h5 className="m-0 mb-0.5 text-[12px] font-bold">{title}</h5>
      <p className="m-0 text-[11.5px] text-muted-foreground leading-[1.45]">{children}</p>
    </div>
  </div>
);

// ----- Layout pipeline ---------------------------------------------------

interface LayoutResult {
  publishers: LaidOutSide[];
  subscribers: LaidOutSide[];
  edges: LaidOutEdge[];
  hasData: boolean;
  maxMessages: number;
}

function buildLayout(
  nodes: TopologyNode[],
  nodeById: Map<string, TopologyNode>,
  flowEdges: FlowEdge[],
): LayoutResult {
  // 1. Collect distinct publisher / subscriber IDs from the edge list — a
  //    node only appears on a side if it actually participates in a route on
  //    that side (mirrors the design's bipartite intent). Endpoints that are
  //    both producers and consumers appear once on each side.
  const pubIds = new Set<string>();
  const subIds = new Set<string>();
  for (const e of flowEdges) {
    pubIds.add(e.from);
    subIds.add(e.to);
  }

  if (pubIds.size === 0 || subIds.size === 0) {
    return {
      publishers: [],
      subscribers: [],
      edges: [],
      hasData: false,
      maxMessages: 0,
    };
  }

  // 2. Build per-side records with display strings, sorted by name so order
  //    is deterministic across re-renders.
  const publishers = Array.from(pubIds)
    .map((id) => sideRecord(id, nodeById, "pub"))
    .sort((a, b) => a.name.localeCompare(b.name));
  const subscribers = Array.from(subIds)
    .map((id) => sideRecord(id, nodeById, "sub"))
    .sort((a, b) => a.name.localeCompare(b.name));

  // 3. Vertical layout — center each column inside the canvas.
  const pubTotalH = publishers.length * NODE_H + (publishers.length - 1) * PUB_GAP;
  const pubStartY = Math.max(40, (VIEWBOX_H - pubTotalH) / 2 + 10);
  const laidOutPubs: LaidOutSide[] = publishers.map((p, i) => {
    const y = pubStartY + i * (NODE_H + PUB_GAP);
    return { ...p, y, mid: y + NODE_H / 2 };
  });
  const subTotalH = subscribers.length * NODE_H + (subscribers.length - 1) * SUB_GAP;
  const subStartY = Math.max(40, (VIEWBOX_H - subTotalH) / 2 + 10);
  const laidOutSubs: LaidOutSide[] = subscribers.map((s, i) => {
    const y = subStartY + i * (NODE_H + SUB_GAP);
    return { ...s, y, mid: y + NODE_H / 2 };
  });

  const pubByIdMap = new Map(laidOutPubs.map((p) => [p.id, p]));
  const subByIdMap = new Map(laidOutSubs.map((s) => [s.id, s]));

  // 4. Translate each edge's `messages` count into a stroke width and stack
  //    the ribbon attachment points on each side. Stacking keeps ribbons
  //    fanning out from the centre of the node rather than crossing each
  //    other inside the body — much easier to read at a glance.
  const maxMessages = flowEdges.reduce((acc, e) => Math.max(acc, e.messages), 0);
  const widthFor = (e: FlowEdge): number => {
    if (e.health === "idle") return IDLE_RIBBON;
    if (maxMessages <= 0) return MIN_RIBBON;
    const ratio = e.messages / maxMessages;
    return MIN_RIBBON + ratio * (MAX_RIBBON - MIN_RIBBON);
  };

  const sortedEdges = flowEdges
    .filter((e) => pubByIdMap.has(e.from) && subByIdMap.has(e.to))
    .map((e) => ({ ...e, strokeWidth: widthFor(e) }));

  // For each publisher, stack outgoing ribbons by target mid (so the ribbon
  // closest to its target stacks on top — minimises crossings).
  for (const pub of laidOutPubs) {
    const outs = sortedEdges
      .filter((e) => e.from === pub.id)
      .sort((a, b) => (subByIdMap.get(a.to)!.mid - subByIdMap.get(b.to)!.mid));
    const totalW =
      outs.reduce((acc, e) => acc + e.strokeWidth, 0) +
      Math.max(0, outs.length - 1) * RIBBON_GAP;
    let cursor = pub.mid - totalW / 2;
    for (const e of outs) {
      (e as LaidOutEdge).srcY = cursor + e.strokeWidth / 2;
      cursor += e.strokeWidth + RIBBON_GAP;
    }
  }
  // Same on the subscriber side — stack by source mid.
  for (const sub of laidOutSubs) {
    const ins = sortedEdges
      .filter((e) => e.to === sub.id)
      .sort((a, b) => (pubByIdMap.get(a.from)!.mid - pubByIdMap.get(b.from)!.mid));
    const totalW =
      ins.reduce((acc, e) => acc + e.strokeWidth, 0) +
      Math.max(0, ins.length - 1) * RIBBON_GAP;
    let cursor = sub.mid - totalW / 2;
    for (const e of ins) {
      (e as LaidOutEdge).tgtY = cursor + e.strokeWidth / 2;
      cursor += e.strokeWidth + RIBBON_GAP;
    }
  }

  return {
    publishers: laidOutPubs,
    subscribers: laidOutSubs,
    edges: sortedEdges as LaidOutEdge[],
    hasData: sortedEdges.length > 0,
    maxMessages,
  };
}

function sideRecord(
  id: string,
  nodeById: Map<string, TopologyNode>,
  side: "pub" | "sub",
): FlowSide {
  const node = nodeById.get(id);
  if (!node) {
    return {
      id,
      name: id,
      team: "—",
      role: "0",
      health: "idle",
      metric: "no data",
      metricKind: "idle",
    };
  }
  const isPub = side === "pub";
  // Per side, surface the most useful number: published count for the
  // publisher card, handled / failed for the subscriber card. Failures on
  // a subscriber dominate the metric so the operator sees the problem
  // immediately without expanding details.
  const role = isPub ? `${node.publishCount}` : `${node.subscribeCount}`;
  let metric: string;
  let metricKind: FlowSide["metricKind"];
  if (isPub) {
    if (node.publishedMessages === 0) {
      metric = "idle";
      metricKind = "idle";
    } else {
      metric = `${node.publishedMessages.toLocaleString()} sent`;
      metricKind = "ok";
    }
  } else {
    if (node.failedMessages > 0) {
      metric = `${node.handledMessages.toLocaleString()} ok · ${node.failedMessages.toLocaleString()} fail`;
      metricKind = "bad";
    } else if (node.handledMessages === 0) {
      metric = "idle";
      metricKind = "idle";
    } else {
      metric = `${node.handledMessages.toLocaleString()} handled`;
      metricKind = "ok";
    }
  }
  return {
    id,
    name: node.name,
    team: node.role || "Endpoint",
    role,
    health: node.health,
    metric,
    metricKind,
  };
}
