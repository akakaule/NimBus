import { useCallback, useEffect, useMemo, useRef, useState } from "react";
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

const MIN_ZOOM = 0.4;
const MAX_ZOOM = 4;
const WHEEL_ZOOM_FACTOR = 1.15;
const BUTTON_ZOOM_FACTOR = 1.25;
// Drag threshold (screen pixels) below which we treat a release as a click,
// so panning the canvas doesn't swallow card selection.
const DRAG_CLICK_THRESHOLD = 4;

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
  const svgRef = useRef<SVGSVGElement | null>(null);
  const tagRef = useRef<HTMLSpanElement | null>(null);

  // Pan/zoom state. `pan` is the top-left corner of the visible viewBox in
  // base coordinates; `zoom` shrinks the visible viewBox (zoom > 1 = closer).
  // Together they drive the SVG `viewBox` attribute — text and strokes stay
  // crisp at any zoom level because we never CSS-transform the SVG.
  const [zoom, setZoom] = useState(1);
  const [pan, setPan] = useState({ x: 0, y: 0 });

  // Per-node position overrides — populated when the operator drags a card.
  // The radial-layout `useMemo` consults this map first and falls back to the
  // computed angle/radius for un-dragged nodes, so edges + pills track the
  // new spot automatically. Cleared by the "reset layout" zoom-overlay button.
  const [nodePositions, setNodePositions] = useState<
    Record<string, { x: number; y: number }>
  >({});

  // Tracks an in-progress drag. Two shapes: a `pan` drag moves the SVG
  // viewBox; a `node` drag moves a single endpoint card. Both record `moved`
  // so the eventual click can be suppressed when the gesture was actually
  // a drag, keeping selection-on-tap intact.
  type DragState =
    | {
        kind: "pan";
        startX: number;
        startY: number;
        panX: number;
        panY: number;
        moved: number;
      }
    | {
        kind: "node";
        nodeId: string;
        startX: number;
        startY: number;
        originX: number;
        originY: number;
        moved: number;
      };
  const dragRef = useRef<DragState | null>(null);

  const viewBoxW = VIEWBOX_W / zoom;
  const viewBoxH = VIEWBOX_H / zoom;
  const viewBoxStr = `${pan.x} ${pan.y} ${viewBoxW} ${viewBoxH}`;

  // Radial layout — angle each endpoint around the hub. Radius scales mildly
  // with N so cards stay angularly separated; clamped wide on the low end
  // so small graphs (N=2-4, the common demo case) feel airy instead of
  // crowded against the bus pill. 250 is the practical ceiling — much
  // larger and top/bottom cards clip the 580-tall viewBox.
  //
  // Operator-set positions (via dragging a card) win over the computed
  // radial position so the graph remembers manual placements across
  // re-renders / data refreshes.
  const layout = useMemo(() => {
    const n = Math.max(nodes.length, 1);
    const radius = Math.min(250, Math.max(210, 60 + n * 20));
    const positions: Record<string, { x: number; y: number; angle: number }> = {};
    nodes.forEach((node, i) => {
      // Start from -π/2 (top) and walk clockwise so the first node lands
      // straight above the hub.
      const angle = -Math.PI / 2 + (i / n) * Math.PI * 2;
      const radial = {
        x: HUB_CX + radius * Math.cos(angle) - CARD_W / 2,
        y: HUB_CY + radius * Math.sin(angle) - CARD_H / 2,
      };
      const override = nodePositions[node.id];
      positions[node.id] = {
        x: override?.x ?? radial.x,
        y: override?.y ?? radial.y,
        angle,
      };
    });
    return positions;
  }, [nodes, nodePositions]);

  // Curve from a card's edge to the hub's edge. We pick the side of the card
  // closest to the hub and the side of the hub closest to the card so the
  // line never visually crosses node bodies.
  //
  // Path *direction* depends on `kind` — publishes flow endpoint → hub,
  // subscribes flow hub → endpoint — so the arrowhead (always painted at
  // `markerEnd`, the path's last point) lands at the destination of the
  // message flow rather than always at the hub.
  const drawCurve = (endpointId: string, kind: "publish" | "subscribe") => {
    const pos = layout[endpointId];
    if (!pos) return "";
    // Card center
    const cx = pos.x + CARD_W / 2;
    const cy = pos.y + CARD_H / 2;
    // Unit vector card → hub; used to pick exit/entry points on each shape.
    const dx = HUB_CX - cx;
    const dy = HUB_CY - cy;
    const len = Math.hypot(dx, dy) || 1;
    const ux = dx / len;
    const uy = dy / len;
    // Anchor a few units OUTSIDE each rect so the 12-unit marker fully clears
    // the box body. Without this gap, refX=11 leaves most of the arrowhead
    // tucked under the destination card (the bug the user spotted).
    const ANCHOR_GAP = 4;
    const cardT = rectExitDistance(ux, uy, CARD_W / 2, CARD_H / 2);
    const hubT = rectExitDistance(ux, uy, HUB_W / 2, HUB_H / 2);
    const cardAnchorX = cx + ux * (cardT + ANCHOR_GAP);
    const cardAnchorY = cy + uy * (cardT + ANCHOR_GAP);
    const hubAnchorX = HUB_CX - ux * (hubT + ANCHOR_GAP);
    const hubAnchorY = HUB_CY - uy * (hubT + ANCHOR_GAP);

    // Pick path direction so the arrowhead lands at the flow destination.
    const isPublish = kind === "publish";
    const startX = isPublish ? cardAnchorX : hubAnchorX;
    const startY = isPublish ? cardAnchorY : hubAnchorY;
    const endX = isPublish ? hubAnchorX : cardAnchorX;
    const endY = isPublish ? hubAnchorY : cardAnchorY;

    // Re-derive direction unit vector and perpendicular against the chosen
    // start → end. Always biasing the curve to +perp of the travel direction
    // means publish and subscribe edges between the same pair bow to
    // opposite sides automatically (because their travel directions are
    // opposite), so we keep the no-overlap arrangement without an explicit
    // sign flip.
    const tx = (endX - startX) / len;
    const ty = (endY - startY) / len;
    const perpX = -ty;
    const perpY = tx;
    const c1x = startX + tx * 50 + perpX * 36;
    const c1y = startY + ty * 50 + perpY * 36;
    const c2x = endX - tx * 50 + perpX * 36;
    const c2y = endY - ty * 50 + perpY * 36;
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
      // While the user is actively dragging the canvas, suppress tooltips —
      // they distract from the pan and follow the cursor jitterily.
      if (dragRef.current) {
        tag.classList.remove("show");
        return;
      }
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

  // Wheel-to-zoom. React's onWheel is passive by default in recent versions,
  // so preventDefault() inside the JSX handler doesn't stop the page from
  // scrolling. Wire it imperatively with `{ passive: false }` so the zoom
  // wholly consumes wheel events over the canvas.
  useEffect(() => {
    const svg = svgRef.current;
    const wrap = wrapRef.current;
    if (!svg || !wrap) return;

    const handleWheel = (e: WheelEvent) => {
      e.preventDefault();
      const r = wrap.getBoundingClientRect();
      const cx = e.clientX - r.left;
      const cy = e.clientY - r.top;
      // Cursor position in current viewBox coordinates — we keep this stable
      // across the zoom so the operator can drill into a node naturally.
      const vx = pan.x + (cx / r.width) * viewBoxW;
      const vy = pan.y + (cy / r.height) * viewBoxH;
      const factor = e.deltaY < 0 ? WHEEL_ZOOM_FACTOR : 1 / WHEEL_ZOOM_FACTOR;
      const nextZoom = clamp(zoom * factor, MIN_ZOOM, MAX_ZOOM);
      if (nextZoom === zoom) return;
      const newW = VIEWBOX_W / nextZoom;
      const newH = VIEWBOX_H / nextZoom;
      setZoom(nextZoom);
      setPan({
        x: vx - (cx / r.width) * newW,
        y: vy - (cy / r.height) * newH,
      });
    };

    svg.addEventListener("wheel", handleWheel, { passive: false });
    return () => svg.removeEventListener("wheel", handleWheel);
  }, [pan.x, pan.y, viewBoxW, viewBoxH, zoom]);

  const handlePointerDown = useCallback(
    (e: React.PointerEvent<SVGSVGElement>) => {
      if (e.button !== 0) return; // left-button only
      const target = e.target as Element | null;
      if (!target) return;

      // Node drag takes precedence: if the pointer-down landed on a card
      // (any descendant of `[data-node]`), start a node-drag for that id.
      const nodeEl = target.closest<SVGElement>("[data-node]");
      if (nodeEl) {
        const nodeId = nodeEl.getAttribute("data-node");
        const pos = nodeId ? layout[nodeId] : undefined;
        if (nodeId && pos) {
          dragRef.current = {
            kind: "node",
            nodeId,
            startX: e.clientX,
            startY: e.clientY,
            originX: pos.x,
            originY: pos.y,
            moved: 0,
          };
          try {
            e.currentTarget.setPointerCapture(e.pointerId);
          } catch {
            // Pointer-capture is best-effort.
          }
          return;
        }
      }

      // Otherwise: skip pan on interactive elements (edges/pills carry
      // `data-tag` for their tooltips), and start a canvas pan on the rest.
      if (target.closest("[data-tag]")) return;
      dragRef.current = {
        kind: "pan",
        startX: e.clientX,
        startY: e.clientY,
        panX: pan.x,
        panY: pan.y,
        moved: 0,
      };
      try {
        e.currentTarget.setPointerCapture(e.pointerId);
      } catch {
        // Some browsers throw if the element is detaching — safe to ignore.
      }
    },
    [pan.x, pan.y, layout],
  );

  const handlePointerMove = useCallback(
    (e: React.PointerEvent<SVGSVGElement>) => {
      const drag = dragRef.current;
      const wrap = wrapRef.current;
      if (!drag || !wrap) return;
      const r = wrap.getBoundingClientRect();
      const dxScreen = e.clientX - drag.startX;
      const dyScreen = e.clientY - drag.startY;
      drag.moved = Math.max(drag.moved, Math.hypot(dxScreen, dyScreen));
      const dxView = dxScreen * (viewBoxW / r.width);
      const dyView = dyScreen * (viewBoxH / r.height);

      if (drag.kind === "pan") {
        setPan({
          x: drag.panX - dxView,
          y: drag.panY - dyView,
        });
      } else {
        // Node drag — the card follows the cursor in viewBox space.
        setNodePositions((prev) => ({
          ...prev,
          [drag.nodeId]: {
            x: drag.originX + dxView,
            y: drag.originY + dyView,
          },
        }));
      }
    },
    [viewBoxW, viewBoxH],
  );

  // Last gesture's total movement (screen px). Read by EndpointCard's onClick
  // wrapper so a click that happened at the end of a pan is suppressed —
  // otherwise releasing a drag over a card would silently switch selection.
  const lastMovedRef = useRef(0);

  const handlePointerUp = useCallback(
    (e: React.PointerEvent<SVGSVGElement>) => {
      const drag = dragRef.current;
      lastMovedRef.current = drag?.moved ?? 0;
      dragRef.current = null;
      try {
        e.currentTarget.releasePointerCapture(e.pointerId);
      } catch {
        // Pointer-capture release is best-effort.
      }
    },
    [],
  );

  // Returns true if the immediately preceding pointer gesture counts as a drag,
  // i.e. moved beyond the click threshold. Cards use this to swallow clicks
  // synthesised at the end of a pan.
  const wasDragging = () => lastMovedRef.current > DRAG_CLICK_THRESHOLD;

  const zoomBy = (factor: number) => {
    const wrap = wrapRef.current;
    if (!wrap) return;
    // Zoom around the geometric center of the visible viewBox so the
    // graph stays balanced.
    const r = wrap.getBoundingClientRect();
    const cx = r.width / 2;
    const cy = r.height / 2;
    const vx = pan.x + (cx / r.width) * viewBoxW;
    const vy = pan.y + (cy / r.height) * viewBoxH;
    const nextZoom = clamp(zoom * factor, MIN_ZOOM, MAX_ZOOM);
    if (nextZoom === zoom) return;
    const newW = VIEWBOX_W / nextZoom;
    const newH = VIEWBOX_H / nextZoom;
    setZoom(nextZoom);
    setPan({
      x: vx - (cx / r.width) * newW,
      y: vy - (cy / r.height) * newH,
    });
  };

  const resetView = () => {
    setZoom(1);
    setPan({ x: 0, y: 0 });
  };

  // Clears every manually-dragged node back to its computed radial position.
  // Kept separate from `resetView` because zoom/pan and node placement are
  // different gestures the operator may want to undo independently.
  const resetLayout = () => {
    setNodePositions({});
  };

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
        ref={svgRef}
        viewBox={viewBoxStr}
        preserveAspectRatio="xMidYMid meet"
        onPointerDown={handlePointerDown}
        onPointerMove={handlePointerMove}
        onPointerUp={handlePointerUp}
        onPointerCancel={handlePointerUp}
        // touch-none kills the browser's pinch/scroll defaults so our gestures win.
        // The cursor swap signals "draggable" when the operator is over background.
        style={{ touchAction: "none" }}
        className={cn(
          "block w-full h-auto select-none",
          dragRef.current ? "cursor-grabbing" : "cursor-grab",
        )}
      >
        <defs>
          <ArrowMarker id="nb-arrow-green" color="var(--nb-success, #2E8F5E)" />
          <ArrowMarker id="nb-arrow-blue" color="var(--nb-info, #3A6FB0)" />
          <ArrowMarker id="nb-arrow-amber" color="var(--nb-warning, #C98A1B)" />
          <ArrowMarker id="nb-arrow-red" color="var(--nb-danger, #C2412E)" />
          <ArrowMarker id="nb-arrow-mute" color="var(--nb-ink-2, #4A463D)" />
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
              onClick={() => {
                if (wasDragging()) return;
                onSelectNode(selectedNodeId === node.id ? undefined : node.id);
              }}
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

      {/* Zoom controls overlay — bottom-right. Trackpad users (or anyone
          without a wheel) need a non-gesture way to navigate. The percentage
          chip mirrors the design system's caps-mono micro-label style. */}
      <div
        className={cn(
          "absolute bottom-3 right-3 z-10",
          "flex flex-col items-stretch gap-1",
          "bg-card border border-border rounded-nb-md shadow-nb-sm",
          "p-1",
        )}
        // Keep clicks here from starting a pan.
        onPointerDown={(e) => e.stopPropagation()}
      >
        <ZoomButton
          label="+"
          onClick={() => zoomBy(BUTTON_ZOOM_FACTOR)}
          disabled={zoom >= MAX_ZOOM}
          title="Zoom in"
        />
        <div className="text-center font-mono text-[10px] text-muted-foreground tabular-nums py-0.5">
          {Math.round(zoom * 100)}%
        </div>
        <ZoomButton
          label="−"
          onClick={() => zoomBy(1 / BUTTON_ZOOM_FACTOR)}
          disabled={zoom <= MIN_ZOOM}
          title="Zoom out"
        />
        <ZoomButton
          label="⤧"
          onClick={resetView}
          disabled={zoom === 1 && pan.x === 0 && pan.y === 0}
          title="Reset view (zoom + pan)"
        />
        <ZoomButton
          label="⌂"
          onClick={resetLayout}
          disabled={Object.keys(nodePositions).length === 0}
          title="Reset node positions"
        />
      </div>
    </div>
  );
};

function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}

// Distance from a rect centre to where the ray (ux, uy) exits the rect.
// Used to anchor edges at the actual rect boundary regardless of which
// side faces the hub — corner rays hit the side they cross first, axis-
// aligned rays hit cleanly. Tiny epsilon dodges the 0/0 case at the centre.
function rectExitDistance(
  ux: number,
  uy: number,
  halfW: number,
  halfH: number,
): number {
  const ax = Math.abs(ux);
  const ay = Math.abs(uy);
  const tx = ax > 1e-6 ? halfW / ax : Number.POSITIVE_INFINITY;
  const ty = ay > 1e-6 ? halfH / ay : Number.POSITIVE_INFINITY;
  return Math.min(tx, ty);
}

interface ZoomButtonProps {
  label: string;
  onClick: () => void;
  disabled?: boolean;
  title: string;
}

const ZoomButton: React.FC<ZoomButtonProps> = ({
  label,
  onClick,
  disabled,
  title,
}) => (
  <button
    type="button"
    onClick={onClick}
    disabled={disabled}
    title={title}
    aria-label={title}
    className={cn(
      "w-7 h-7 inline-flex items-center justify-center rounded-md",
      "text-[14px] font-semibold leading-none select-none",
      "text-foreground hover:bg-muted disabled:opacity-40 disabled:cursor-not-allowed",
      "transition-colors",
    )}
  >
    {label}
  </button>
);

const ArrowMarker: React.FC<{ id: string; color: string }> = ({ id, color }) => (
  // `userSpaceOnUse` decouples marker size from stroke-width, so even thin
  // 1.5px lines get a confidently sized arrowhead. The triangle has a slight
  // back-notch so the direction reads as a chevron rather than a dot at the
  // end of a line — much more legible at small viewport sizes.
  <marker
    id={id}
    viewBox="0 0 12 12"
    refX={11}
    refY={6}
    markerUnits="userSpaceOnUse"
    markerWidth={12}
    markerHeight={12}
    orient="auto-start-reverse"
  >
    <path d="M 0 0 L 12 6 L 0 12 L 3 6 Z" fill={color} />
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
    idle: "url(#nb-arrow-mute)",
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
      strokeWidth={1.8}
      fill="none"
      className={cn(
        // `stroke-opacity` (not the catch-all `opacity`) so the marker's
        // fill stays fully saturated and the arrowhead remains readable
        // even when the line itself is dimmed for idle edges. Idle is the
        // demo's default state so we lean toward visibility here: chunkier
        // 4-4 dashes + 0.7 opacity reads as "alive but quiet" without
        // disappearing into the dotted-grid background.
        "transition-[stroke-opacity]",
        finalColorClass,
        edge.health === "idle"
          ? "[stroke-opacity:0.7] [stroke-dasharray:4_4]"
          : "[stroke-opacity:0.85] hover:[stroke-opacity:1]",
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
    // Position transform lives on the outer <g> as an SVG attribute so it
    // can never be clobbered by a CSS `transform` rule on hover. Interactive
    // styling (cursor, lift) lives on the inner <g> via CSS; the two layers
    // compose instead of fighting (the bug that snapped cards to (0,0) on
    // hover before this split).
    <g transform={`translate(${x}, ${y})`}>
      <g
        onClick={onClick}
        data-tag={tooltip}
        data-node={node.id}
        className="cursor-grab active:cursor-grabbing"
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
