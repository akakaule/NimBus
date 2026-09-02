import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import { Link } from "react-router-dom";
import type { TopologyData } from "components/topology/types";
import type { StatusSnapshot } from "components/flow/types";
import {
  buildSpineModel,
  computeSpineEdges,
  formatRate,
  spineFocusLine,
} from "components/flow/spine-layout";
import type { SpineRect } from "components/flow/spine-layout";
import { cn } from "lib/utils";

// Lanes view for the Flow page (design exploration 1b — "route through the
// event types"). The middle column is what is actually on the bus: publisher →
// event type → subscriber turns N×M crossings into two short, mostly parallel
// hops, and the spine carries its own in/out rates and failure counts. The
// cards are plain HTML (measured after layout); the SVG overlay draws the
// weighted, status-colored lanes between the measured rects — the same
// mechanism the approved prototype used. Hover any card to light its lane end
// to end; the footer line narrates the hovered node.

export interface SpineViewProps {
  topology: TopologyData;
  /** Endpoints to render; undefined = all (mirrors the dots view's filter). */
  visibleEndpointIds?: ReadonlySet<string>;
  /** Non-empty narrows the spine to one event type's lanes. */
  eventType: string;
  /** Window length backing the metrics counts, for per-minute rates. */
  periodMinutes: number;
  /** Display label of the window, e.g. "1h". */
  periodLabel: string;
  /** Live per-endpoint counts — subscriber cards prefer these when present. */
  snapshots: Record<string, StatusSnapshot>;
}

export const SpineView = ({
  topology,
  visibleEndpointIds,
  eventType,
  periodMinutes,
  periodLabel,
  snapshots,
}: SpineViewProps) => {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const [rects, setRects] = useState<Record<string, SpineRect>>({});
  const [focus, setFocus] = useState<string | null>(null);
  const [reducedMotion] = useState(
    () =>
      typeof window !== "undefined" &&
      window.matchMedia("(prefers-reduced-motion: reduce)").matches,
  );

  const model = useMemo(
    () =>
      buildSpineModel(topology, {
        visibleEndpointIds,
        eventType: eventType || undefined,
        periodMinutes,
      }),
    [topology, visibleEndpointIds, eventType, periodMinutes],
  );

  // Card rects, relative to the lanes container. Measured after every commit
  // that can move a card (model change), on container resize, and once the
  // web fonts settle (Manrope's metrics shift the card heights slightly).
  const measure = useCallback(() => {
    const host = containerRef.current;
    if (host === null) return;
    const hostBox = host.getBoundingClientRect();
    if (hostBox.width === 0) return;
    const next: Record<string, SpineRect> = {};
    host.querySelectorAll<HTMLElement>("[data-node]").forEach((el) => {
      const key = el.dataset.node;
      if (key === undefined) return;
      const box = el.getBoundingClientRect();
      next[key] = {
        x: box.left - hostBox.left,
        y: box.top - hostBox.top,
        w: box.width,
        h: box.height,
      };
    });
    setRects((prev) =>
      JSON.stringify(prev) === JSON.stringify(next) ? prev : next,
    );
  }, []);

  useLayoutEffect(() => {
    measure();
  }, [measure, model]);

  useEffect(() => {
    const host = containerRef.current;
    let observer: ResizeObserver | undefined;
    if (host !== null && typeof ResizeObserver !== "undefined") {
      observer = new ResizeObserver(() => measure());
      observer.observe(host);
    }
    window.addEventListener("resize", measure);
    // document.fonts is not in every test environment; guard defensively.
    document.fonts?.ready?.then(measure).catch(() => undefined);
    return () => {
      observer?.disconnect();
      window.removeEventListener("resize", measure);
    };
  }, [measure]);

  const { edges, rings } = useMemo(
    () =>
      computeSpineEdges(rects, model.lanes, {
        focus,
        animate: !reducedMotion,
      }),
    [rects, model, focus, reducedMotion],
  );

  const handleOver = useCallback((event: React.MouseEvent) => {
    const el = (event.target as Element).closest?.("[data-node]");
    const key = el instanceof HTMLElement ? (el.dataset.node ?? null) : null;
    setFocus((prev) => (prev === key ? prev : key));
  }, []);
  const handleLeave = useCallback(() => setFocus(null), []);

  const focusLine = spineFocusLine(model, focus);

  return (
    <div className="bg-card border border-border rounded-nb-lg overflow-hidden text-foreground">
      <style>{`
        @keyframes nb-spine-dash { to { stroke-dashoffset: -160; } }
        .nb-spine-dash { animation: nb-spine-dash 4s linear infinite; }
      `}</style>
      <div className="flex items-center justify-between gap-4 px-4 py-3 border-b border-border">
        <span className="font-mono text-[10px] font-semibold tracking-[0.12em] uppercase text-muted-foreground">
          Event-type spine · {model.types.length} type
          {model.types.length === 1 ? "" : "s"} carrying{" "}
          {formatRate(model.totalRate)}/min
        </span>
        <span className="font-mono text-[11px] text-muted-foreground">
          grouped by namespace · avg over {periodLabel}
        </span>
      </div>

      {model.types.length === 0 ? (
        <p className="m-0 px-4 py-8 text-[13px] text-muted-foreground">
          No event types carry traffic under the current filters. Widen the
          endpoint or event-type filter to see the spine.
        </p>
      ) : (
        <div className="relative px-5 py-5 overflow-x-auto">
          <div className="min-w-[860px]">
            <div className="flex justify-between pb-3">
              <ColumnHeader>Publishers</ColumnHeader>
              <ColumnHeader>Event types</ColumnHeader>
              <ColumnHeader>Subscribers</ColumnHeader>
            </div>
            <div
              ref={containerRef}
              onMouseOver={handleOver}
              onMouseLeave={handleLeave}
              className="relative flex justify-between items-center min-h-[420px]"
            >
              <svg
                aria-hidden="true"
                className="absolute inset-0 w-full h-full pointer-events-none"
                style={{ overflow: "visible" }}
              >
                {edges.map((e) => (
                  <path
                    key={e.key}
                    d={e.d}
                    fill="none"
                    stroke={e.color}
                    strokeWidth={e.w}
                    strokeOpacity={e.op}
                    strokeLinecap="round"
                  />
                ))}
                {edges.map(
                  (e) =>
                    e.dop > 0 && (
                      <path
                        key={`${e.key}::dash`}
                        d={e.d}
                        fill="none"
                        stroke={e.color}
                        strokeWidth={e.dw}
                        strokeOpacity={e.dop}
                        strokeLinecap="round"
                        strokeDasharray="2 22"
                        className="nb-spine-dash"
                      />
                    ),
                )}
                {rings.map((r) => (
                  <rect
                    key={r.key}
                    x={r.x}
                    y={r.y}
                    width={r.w}
                    height={r.h}
                    rx={9}
                    fill="none"
                    stroke="#E8743C"
                    strokeWidth={1.5}
                  />
                ))}
              </svg>

              <div className="relative flex flex-col gap-4 w-[236px] shrink-0">
                {model.publishers.map((p) => (
                  <div
                    key={p.key}
                    data-node={p.key}
                    className="bg-card border border-border rounded-nb-md px-3 py-2.5 flex flex-col gap-1"
                  >
                    <Link
                      to={`/Endpoints/Details/${encodeURIComponent(p.endpointId)}`}
                      className="font-bold text-[14px] leading-tight text-status-info no-underline hover:text-primary truncate"
                    >
                      {p.name}
                    </Link>
                    <span className="font-mono text-[10.5px] leading-none text-muted-foreground tabular-nums">
                      {formatRate(p.rate)}/min · {p.typeCount} type
                      {p.typeCount === 1 ? "" : "s"}
                    </span>
                  </div>
                ))}
              </div>

              <div className="relative flex flex-col gap-3.5 w-[300px] shrink-0">
                {model.types.map((t) => (
                  <div
                    key={t.key}
                    data-node={t.key}
                    className="bg-card border border-border rounded-nb-md px-3 py-2.5 flex flex-col gap-1.5"
                  >
                    <div className="flex items-center justify-between gap-2">
                      {t.namespace !== "" ? (
                        <span className="font-mono text-[9.5px] font-semibold tracking-[0.1em] uppercase text-nimbus-purple bg-nimbus-purple-50 px-1.5 py-[3px] rounded-nb-sm truncate">
                          {t.namespace}
                        </span>
                      ) : (
                        <span />
                      )}
                      <span className="font-mono text-[11px] font-medium tabular-nums shrink-0">
                        {formatRate(t.rate)}/min
                      </span>
                    </div>
                    <Link
                      to={`/EventTypes/Details/${encodeURIComponent(t.eventTypeId)}`}
                      className="font-bold text-[14px] leading-tight text-foreground no-underline hover:text-primary truncate"
                    >
                      {t.label}
                    </Link>
                    <span className="font-mono text-[10.5px] leading-none text-muted-foreground tabular-nums">
                      {t.producers} in · {t.consumers} out ·{" "}
                      {t.failed > 0 ? (
                        <span className="text-status-danger">
                          {t.failed.toLocaleString()} failing
                        </span>
                      ) : (
                        "no failures"
                      )}
                    </span>
                  </div>
                ))}
              </div>

              <div className="relative flex flex-col gap-[11px] w-[236px] shrink-0">
                {model.subscribers.map((s) => {
                  const failed = snapshots[s.endpointId]?.failed ?? s.failed;
                  return (
                    <div
                      key={s.key}
                      data-node={s.key}
                      className={cn(
                        "bg-card border rounded-nb-md px-3 py-2.5 flex flex-col gap-1",
                        failed > 0 ? "border-status-danger" : "border-border",
                      )}
                    >
                      <Link
                        to={`/Endpoints/Details/${encodeURIComponent(s.endpointId)}`}
                        className="font-bold text-[14px] leading-tight text-status-info no-underline hover:text-primary truncate"
                      >
                        {s.name}
                      </Link>
                      <span className="font-mono text-[10.5px] leading-none text-muted-foreground tabular-nums">
                        {formatRate(s.rate)}/min ·{" "}
                        {failed > 0 ? (
                          <span className="text-status-danger">
                            {failed.toLocaleString()} failed
                          </span>
                        ) : (
                          "no failures"
                        )}
                      </span>
                    </div>
                  );
                })}
              </div>
            </div>
          </div>
        </div>
      )}

      <div className="flex items-center gap-2.5 px-4 py-2.5 border-t border-border bg-background min-h-[20px]">
        {focus !== null && focusLine !== "" ? (
          <span className="font-mono text-[12px] font-medium text-foreground">
            {focusLine}
          </span>
        ) : (
          <span className="font-mono text-[12px] text-muted-foreground">
            Hover a publisher, event type or subscriber to light its lane.
          </span>
        )}
      </div>
    </div>
  );
};

const ColumnHeader = ({ children }: { children: React.ReactNode }) => (
  <span className="font-mono text-[10px] font-semibold tracking-[0.12em] uppercase text-muted-foreground">
    {children}
  </span>
);
