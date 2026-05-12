import { useEffect, useMemo, useRef, useState } from "react";
import { cn } from "lib/utils";
import type { EventPill, TopologyEdge, TopologyNode } from "./types";

const VIEWBOX_W = 880;
const VIEWBOX_H = 580;
const CARD_W = 160;
const CARD_H = 80;
const HUB_W = 120;
const HUB_H = 60;
const HUB_CX = VIEWBOX_W / 2;
const HUB_CY = VIEWBOX_H / 2;

interface TopologyGraphProps {
  nodes: TopologyNode[];
  edges: TopologyEdge[];
  pills: EventPill[];
  selectedNodeId?: string;
  onSelectNode: (id: string | undefined) => void;
  className?: string;
}

/**
 * SVG topology graph — bus hub at the centre, endpoint cards arranged on a
 * circle around it, curved edges flowing outward (publish, green) or inward
 * (subscribe, blue). Failing edges go red, idle ones dashed-muted. Hover
 * any edge / pill / card to surface a tooltip; click a card to select it
 * (selection state lives in the parent so the inspector can re-render).
 */
export const TopologyGraph: React.FC<TopologyGraphProps> = ({
  nodes,
  edges,
  pills,
  selectedNodeId,
  onSelectNode,
  className,
}) => {
  const wrapRef = useRef<HTMLDivElement | null>(null);
  const tagRef = useRef<HTMLSpanElement | null>(null);

  // Radial layout — angle each endpoint around the hub. Radius shrinks
  // slightly as N grows so cards stay inside the viewbox; clamped so the
  // graph doesn't visually compress at small N.
  const layout = useMemo(() => {
    const n = Math.max(nodes.length, 1);
    const radius = Math.min(220, Math.max(160, 60 + n * 18));
    const positions: Record<string, { x: number; y: number; angle: number }> = {};
    nodes.forEach((node, i) => {
      // Start from -π/2 (top) and walk clockwise so the first node lands
      // straight above the hub.
      const angle = -Math.PI / 2 + (i / n) * Math.PI * 2;
      positions[node.id] = {
        x: HUB_CX + radius * Math.cos(angle) - CARD_W / 2,
        y: HUB_CY + radius * Math.sin(angle) - CARD_H / 2,
        angle,
      };
    });
    return positions;
  }, [nodes]);

  // Curve from a card's edge to the hub's edge. We pick the side of the card
  // closest to the hub and the side of the hub closest to the card so the
  // line never visually crosses node bodies.
  const drawCurve = (endpointId: string, kind: "publish" | "subscribe") => {
    const pos = layout[endpointId];
    if (!pos) return "";
    // Card center
    const cx = pos.x + CARD_W / 2;
    const cy = pos.y + CARD_H / 2;
    // Pick exit point on the card facing the hub
    const dx = HUB_CX - cx;
    const dy = HUB_CY - cy;
    const len = Math.hypot(dx, dy) || 1;
    const ux = dx / len;
    const uy = dy / len;
    const startX = cx + ux * (CARD_W / 2 - 6);
    const startY = cy + uy * (CARD_H / 2 - 6);
    const endX = HUB_CX - ux * (HUB_W / 2 - 6);
    const endY = HUB_CY - uy * (HUB_H / 2 - 6);
    // Control points bend the curve outward so two opposite edges between
    // the same pair don't visually overlap. We offset perpendicular to the
    // straight line by ~40px, flipped for publish vs subscribe.
    const perpX = -uy;
    const perpY = ux;
    const sign = kind === "publish" ? 1 : -1;
    const c1x = startX + ux * 50 + perpX * 36 * sign;
    const c1y = startY + uy * 50 + perpY * 36 * sign;
    const c2x = endX - ux * 50 + perpX * 36 * sign;
    const c2y = endY - uy * 50 + perpY * 36 * sign;
    return `M ${startX} ${startY} C ${c1x} ${c1y}, ${c2x} ${c2y}, ${endX} ${endY}`;
  };

  // Compute pill anchor positions — sit them on the midpoint of the parent
  // edge. We approximate the midpoint of a cubic curve by sampling t=0.5.
  const pillPositions = useMemo(() => {
    return pills.map((pill) => {
      const pos = layout[pill.anchorEndpointId];
      if (!pos) return { pill, x: HUB_CX, y: HUB_CY };
      const cx = pos.x + CARD_W / 2;
      const cy = pos.y + CARD_H / 2;
      const dx = HUB_CX - cx;
      const dy = HUB_CY - cy;
      const sign = pill.kind === "publish" ? 1 : -1;
      // Approximate midpoint with a small perpendicular bias matching the
      // curve's control offset, so labels track the curve's apex.
      const midX = cx + dx * 0.5;
      const midY = cy + dy * 0.5;
      const len = Math.hypot(dx, dy) || 1;
      const perpX = (-dy / len) * 22 * sign;
      const perpY = (dx / len) * 22 * sign;
      return { pill, x: midX + perpX, y: midY + perpY };
    });
  }, [pills, layout]);

  // Tooltip plumbing — one floating <span> outside the SVG. Any node/pill/edge
  // emits `data-tag` and we render that text on mousemove.
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

  return (
    <div
      ref={wrapRef}
      className={cn(
        "relative bg-card border border-border rounded-nb-lg overflow-hidden",
        // Dotted-grid background matching the design's canvas-wrap rule
        "bg-[radial-gradient(circle,#E5DFCE_1px,transparent_1px),radial-gradient(circle,#E5DFCE_1px,transparent_1px)]",
        "bg-[length:24px_24px,24px_24px] bg-[position:0_0,12px_12px]",
        "dark:bg-[radial-gradient(circle,#2A2620_1px,transparent_1px),radial-gradient(circle,#2A2620_1px,transparent_1px)]",
        className,
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
      <svg
        viewBox={`0 0 ${VIEWBOX_W} ${VIEWBOX_H}`}
        preserveAspectRatio="xMidYMid meet"
        className="block w-full h-auto"
      >
        <defs>
          <ArrowMarker id="nb-arrow-green" color="var(--nb-success, #2E8F5E)" />
          <ArrowMarker id="nb-arrow-blue" color="var(--nb-info, #3A6FB0)" />
          <ArrowMarker id="nb-arrow-amber" color="var(--nb-warning, #C98A1B)" />
          <ArrowMarker id="nb-arrow-red" color="var(--nb-danger, #C2412E)" />
          <ArrowMarker id="nb-arrow-mute" color="var(--nb-ink-3, #8A8473)" />
        </defs>

        {/* Edges first so nodes paint over them */}
        {edges.map((edge) => (
          <EdgePath
            key={edge.id}
            d={drawCurve(edge.endpointId, edge.kind)}
            edge={edge}
          />
        ))}

        {/* Event-type pills floating on the busiest edges */}
        {pillPositions.map(({ pill, x, y }) => (
          <EventTypePill key={pill.id} pill={pill} x={x} y={y} />
        ))}

        {/* Endpoint cards */}
        {nodes.map((node) => {
          const pos = layout[node.id];
          if (!pos) return null;
          return (
            <EndpointCard
              key={node.id}
              node={node}
              x={pos.x}
              y={pos.y}
              selected={selectedNodeId === node.id}
              onClick={() =>
                onSelectNode(selectedNodeId === node.id ? undefined : node.id)
              }
            />
          );
        })}

        {/* Bus hub */}
        <g transform={`translate(${HUB_CX - HUB_W / 2}, ${HUB_CY - HUB_H / 2})`}>
          <rect width={HUB_W} height={HUB_H} rx={HUB_H / 2} fill="#1A1814" />
          <text
            x={HUB_W / 2}
            y={HUB_H / 2 - 4}
            textAnchor="middle"
            fill="#F4F2EA"
            fontFamily="Manrope, sans-serif"
            fontWeight={800}
            fontSize={13}
          >
            NimBus
          </text>
          <text
            x={HUB_W / 2}
            y={HUB_H / 2 + 14}
            textAnchor="middle"
            fill="#8A8473"
            fontFamily="JetBrains Mono, monospace"
            fontSize={10}
            letterSpacing="0.1em"
          >
            EVENT BUS
          </text>
        </g>
      </svg>
    </div>
  );
};

const ArrowMarker: React.FC<{ id: string; color: string }> = ({ id, color }) => (
  <marker
    id={id}
    viewBox="0 0 10 10"
    refX={9}
    refY={5}
    markerWidth={6}
    markerHeight={6}
    orient="auto-start-reverse"
  >
    <path d="M 0 0 L 10 5 L 0 10 z" fill={color} />
  </marker>
);

interface EdgePathProps {
  d: string;
  edge: TopologyEdge;
}

const EdgePath: React.FC<EdgePathProps> = ({ d, edge }) => {
  const colorClass = {
    healthy: "stroke-[var(--nb-success,#2E8F5E)]",
    warn: "stroke-[var(--nb-warning,#C98A1B)]",
    fail: "stroke-[var(--nb-danger,#C2412E)]",
    idle: "stroke-[var(--nb-ink-3,#8A8473)]",
  }[edge.health];
  const markerEnd = {
    healthy: "url(#nb-arrow-green)",
    warn: "url(#nb-arrow-amber)",
    fail: "url(#nb-arrow-red)",
    idle: edge.kind === "subscribe"
      ? "url(#nb-arrow-mute)"
      : "url(#nb-arrow-mute)",
  }[edge.health];
  // Subscribe edges paint blue when healthy (matches design legend: blue = subscribes).
  const isSubscribeHealthy = edge.kind === "subscribe" && edge.health === "healthy";
  const finalColorClass = isSubscribeHealthy
    ? "stroke-[var(--nb-info,#3A6FB0)]"
    : colorClass;
  const finalMarker = isSubscribeHealthy ? "url(#nb-arrow-blue)" : markerEnd;
  const tooltip = formatEdgeTooltip(edge);

  return (
    <path
      d={d}
      data-tag={tooltip}
      strokeWidth={1.6}
      fill="none"
      className={cn(
        "transition-opacity",
        finalColorClass,
        edge.health === "idle"
          ? "opacity-40 [stroke-dasharray:2_4]"
          : "opacity-75 hover:opacity-100",
        edge.health !== "idle" && "nb-flow-anim",
        edge.health === "warn" && "[stroke-dasharray:4_3]",
      )}
      markerEnd={finalMarker}
    />
  );
};

function formatEdgeTooltip(edge: TopologyEdge): string {
  const verb = edge.kind === "publish" ? "publishes" : "subscribes";
  const count = edge.eventTypeIds.length;
  const types = count === 1 ? "1 event type" : `${count} event types`;
  const msgs =
    edge.messages > 0
      ? ` · ${edge.messages.toLocaleString()} msgs`
      : "";
  return `${edge.endpointId} ${verb} ${types}${msgs}`;
}

interface EndpointCardProps {
  node: TopologyNode;
  x: number;
  y: number;
  selected: boolean;
  onClick: () => void;
}

const EndpointCard: React.FC<EndpointCardProps> = ({
  node,
  x,
  y,
  selected,
  onClick,
}) => {
  const headFill = {
    good: "var(--nb-success-50, #DCEFE4)",
    warn: "var(--nb-warning-50, #F6E7C7)",
    bad: "var(--nb-danger-50, #F4D9D3)",
    idle: "var(--nb-surface-2, #ECE7DA)",
  }[node.health];
  const pulseFill = {
    good: "var(--nb-success, #2E8F5E)",
    warn: "var(--nb-warning, #C98A1B)",
    bad: "var(--nb-danger, #C2412E)",
    idle: "var(--nb-ink-3, #8A8473)",
  }[node.health];
  const statColor = {
    good: "var(--nb-success, #2E8F5E)",
    warn: "var(--nb-warning, #C98A1B)",
    bad: "var(--nb-danger, #C2412E)",
    idle: "var(--nb-ink-3, #8A8473)",
  }[node.health];
  const tooltip = `${node.name} · P:${node.publishCount} S:${node.subscribeCount} · ${node.failedMessages} failed`;

  const stats = formatNodeStats(node);

  return (
    <g
      transform={`translate(${x}, ${y})`}
      onClick={onClick}
      data-tag={tooltip}
      className="cursor-pointer transition-transform hover:-translate-y-px"
    >
      <rect
        x={0}
        y={0}
        width={CARD_W}
        height={CARD_H}
        rx={10}
        fill="var(--nb-bg, #F4F2EA)"
        stroke={selected ? "var(--nb-primary, #E8743C)" : "var(--nb-border-strong, #C9C1AB)"}
        strokeWidth={selected ? 2 : 1.5}
      />
      <rect x={0} y={0} width={CARD_W} height={22} rx={10} fill={headFill} />
      <rect x={0} y={12} width={CARD_W} height={10} fill={headFill} />
      <circle cx={12} cy={11} r={3} fill={pulseFill} />
      <text
        x={22}
        y={14}
        fontFamily="JetBrains Mono, monospace"
        fontSize={9.5}
        fill="var(--nb-ink-3, #8A8473)"
        letterSpacing="0.06em"
      >
        Endpoint
      </text>
      <text
        x={12}
        y={42}
        fontFamily="Manrope, sans-serif"
        fontWeight={700}
        fontSize={13}
        fill="var(--nb-ink, #1A1814)"
      >
        {truncate(node.name, 18)}
      </text>
      <text
        x={12}
        y={62}
        fontFamily="JetBrains Mono, monospace"
        fontSize={10.5}
        fill="var(--nb-ink-2, #4A463D)"
      >
        P: {node.publishCount}  ·  S: {node.subscribeCount}
      </text>
      <text
        x={12}
        y={74}
        fontFamily="JetBrains Mono, monospace"
        fontSize={10.5}
        fill={statColor}
      >
        {stats}
      </text>
    </g>
  );
};

function formatNodeStats(node: TopologyNode): string {
  if (
    node.health === "idle" ||
    (node.handledMessages === 0 && node.failedMessages === 0)
  ) {
    return "no traffic";
  }
  const ok = (node.handledMessages - node.failedMessages).toLocaleString();
  const failed = node.failedMessages.toLocaleString();
  return `${ok} ok · ${failed} failed`;
}

function truncate(s: string, n: number): string {
  return s.length > n ? `${s.slice(0, n - 1)}…` : s;
}

interface PillProps {
  pill: EventPill;
  x: number;
  y: number;
}

const EventTypePill: React.FC<PillProps> = ({ pill, x, y }) => {
  // Heuristic width — character count * average char width + padding
  const w = Math.max(80, pill.label.length * 7.5 + 20);
  const h = 22;
  return (
    <g
      transform={`translate(${x - w / 2}, ${y - h / 2})`}
      data-tag={pill.tooltip}
      className="cursor-pointer"
    >
      <rect
        x={0}
        y={0}
        width={w}
        height={h}
        rx={h / 2}
        fill="var(--nb-purple-50, #ECE2F4)"
        stroke="#D6BFE9"
      />
      <text
        x={w / 2}
        y={h / 2 + 4}
        textAnchor="middle"
        fontFamily="JetBrains Mono, monospace"
        fontSize={10.5}
        fontWeight={600}
        fill="var(--nb-purple, #6B3FA3)"
      >
        {truncate(pill.label, 24)}
      </text>
    </g>
  );
};
