import { useEffect, useMemo, useState } from "react";
import moment from "moment";
import { cn } from "lib/utils";
import { useEnv, getApplicationStatus } from "hooks/app-status";
import {
  useMonitorData,
  type FleetSample,
  type MonitorEndpoint,
  type TickerEvent,
} from "hooks/use-monitor-data";

/* Theme-aware status tints. The rest of the app pairs a dark "ink" hue for
   the cream light theme with a lifted tint for warm-black dark mode; the
   wall reuses exactly those pairs so its palette never drifts from the app's.
   Surfaces/borders/text go through the shared tokens (background, card,
   border, foreground, muted) so /Monitor follows the theme toggle. Every
   fill is flat — no gradients. */
const DANGER_TEXT = "text-status-danger-ink dark:text-[#E4A096]";
const WARNING_TEXT = "text-status-warning-ink dark:text-[#E7C783]";
const SUCCESS_TEXT = "text-status-success-ink dark:text-[#7FD2A6]";

/** Backlog (pending + deferred) at which an endpoint starts shouting. */
export const BACKLOG_ELEVATED = 100;
/** Backlog at which it gets the loudest treatment the amber band has. */
export const BACKLOG_HIGH = 500;

/**
 * One size knob for the whole wall: every dimension below is in `em`, so the
 * page scales with the screen — about 15 px on a 1080p display and 30 px on
 * 4K — instead of shrinking to desktop-sized text on a large monitor.
 */
const WALL_FONT_SIZE = "clamp(12px, 0.78vw, 30px)";

export type BacklogLevel = "normal" | "elevated" | "high";

/** How the fleet ring and legend classify an endpoint. */
export type EndpointState = "nogo" | "acked" | "hold" | "go";

/**
 * Live Status Monitor — designed for a wall display, not a desktop tool.
 *
 *  - Header: environment, T+ since the first open failure, local + UTC clock
 *  - KPI strip: GO ring (one segment per endpoint), failing / backlog /
 *    failed totals, and a 10-minute fleet telemetry chart
 *  - Failing band sorted by impact, with hero numbers + ACK
 *  - Backlog band for amber pending/deferred (≠ failed), next to the
 *    healthy endpoints so green endpoints don't burn pixels
 *  - Footer: legend, ticker of recent events and a refresh heartbeat
 *  - Stale-data banner if auto-refresh dies — the worst monitoring failure
 *    is a frozen page that looks healthy.
 */
export default function Monitor() {
  const data = useMonitorData();
  const envFromHook = useEnv();
  const [tenantLabel, setTenantLabel] = useState<string | undefined>();
  const [now, setNow] = useState(() => new Date());
  const [showCursor, setShowCursor] = useState(true);

  useEffect(() => {
    const handle = window.setInterval(() => setNow(new Date()), 1000);
    return () => window.clearInterval(handle);
  }, []);

  useEffect(() => {
    void getApplicationStatus().then((s) =>
      setTenantLabel(s?.platformName || undefined),
    );
  }, []);

  // Auto-hide the cursor after 5 s of stillness so the wall display doesn't
  // burn in a stray pointer. Any mouse activity brings it back instantly.
  useEffect(() => {
    let timeout: number | undefined;
    const handleMove = () => {
      setShowCursor(true);
      if (timeout) window.clearTimeout(timeout);
      timeout = window.setTimeout(() => setShowCursor(false), 5000);
    };
    handleMove();
    window.addEventListener("mousemove", handleMove);
    return () => {
      window.removeEventListener("mousemove", handleMove);
      if (timeout) window.clearTimeout(timeout);
    };
  }, []);

  const bands = useMemo(() => splitIntoBands(data.endpoints), [data.endpoints]);
  const summary = useMemo(
    () => summarize(data.endpoints),
    [data.endpoints],
  );
  const openSince = firstOpenFailureAt(data.endpoints);
  const env = envFromHook ?? "dev";
  const nowMs = now.getTime();

  return (
    <div
      className={cn(
        "min-h-screen flex flex-col gap-[0.7em] p-[0.9em]",
        "bg-background text-foreground font-sans",
        "[font-feature-settings:'tnum']",
        showCursor ? "" : "cursor-none",
      )}
      style={{ fontSize: WALL_FONT_SIZE }}
    >
      {data.isStale && (
        <StaleBanner lastRefreshAt={data.lastRefreshAt} now={nowMs} />
      )}
      <div
        className={cn(
          "flex flex-col gap-[0.7em] transition-[opacity,filter]",
          data.isStale ? "opacity-70 grayscale" : "opacity-100",
        )}
      >
        <Hero
          env={env}
          tenant={tenantLabel}
          clock={now}
          unackedFresh={summary.unackedFreshFailures}
          openSince={openSince}
        />
        <KpiStrip
          endpoints={data.endpoints}
          summary={summary}
          telemetry={data.telemetry}
        />
        <FailingBand
          endpoints={bands.failing}
          total={data.endpoints.length}
          onAck={data.ack}
          onUnack={data.unack}
          now={nowMs}
        />
        {(bands.watching.length > 0 || bands.healthy.length > 0) && (
          <div
            className={cn(
              "grid gap-[0.7em] grid-cols-1",
              bands.watching.length > 0 &&
                bands.healthy.length > 0 &&
                "lg:grid-cols-[3fr_2fr]",
            )}
          >
            <WatchingBand
              endpoints={bands.watching}
              lastRefreshAt={data.lastRefreshAt}
              now={nowMs}
            />
            <HealthyStrip endpoints={bands.healthy} />
          </div>
        )}
        <WallFoot
          ticker={data.ticker}
          lastRefreshAt={data.lastRefreshAt}
          isStale={data.isStale}
          now={nowMs}
        />
      </div>
    </div>
  );
}

/* =====================================================================
   Header
   ===================================================================== */

interface HeroProps {
  env: string;
  tenant?: string;
  clock: Date;
  unackedFresh: number;
  openSince: number | undefined;
}

const Hero = ({ env, tenant, clock, unackedFresh, openSince }: HeroProps) => {
  const upperEnv = env.toUpperCase();
  // PROD environments get a red-tinted pill because that's the room's most
  // load-bearing question after "is anything broken?" — anything else just
  // borrows the warning amber so it still reads as "this is not prod".
  const envClass =
    upperEnv === "PROD" || upperEnv === "PRD"
      ? cn("bg-status-danger/[0.14] border-status-danger/50", DANGER_TEXT)
      : cn("bg-status-warning/[0.12] border-status-warning/50", WARNING_TEXT);

  return (
    <header className="flex flex-wrap items-center gap-x-[1.6em] gap-y-[0.8em] px-[1.4em] py-[0.9em] rounded-nb-md bg-card border border-border">
      <div className="inline-flex items-center gap-[0.35em] pr-[0.9em] border-r border-border text-[1.8em] font-extrabold tracking-tight">
        <LogoMark />
        <span>
          NimBus<span className="text-primary">.</span>
        </span>
      </div>
      <div className="min-w-0">
        <div className="text-[1.35em] font-bold tracking-tight">
          Live status monitor
        </div>
        <div className="font-mono text-[0.85em] text-muted-foreground mt-[0.3em]">
          {tenant ? `${tenant} · ` : ""}Auto-refresh every 5 s · ACK to silence
          known failures
          {unackedFresh > 0 && (
            <span className={DANGER_TEXT}>
              {` · ${unackedFresh} new failure${unackedFresh === 1 ? "" : "s"}`}
            </span>
          )}
        </div>
      </div>
      <div className="flex-1" />
      <span
        className={cn(
          "font-mono text-[0.9em] tracking-[0.16em] uppercase font-bold",
          "px-[0.9em] py-[0.45em] rounded-nb-sm border",
          "inline-flex items-center gap-[0.5em]",
          envClass,
        )}
      >
        <Pulser />
        {upperEnv}
      </span>
      <div className="pl-[1.6em] border-l border-border">
        <Label>{openSince ? "T+ first failure seen" : "No open failures"}</Label>
        <div
          className={cn(
            "font-mono font-medium text-[2.6em] leading-none mt-[0.15em]",
            openSince ? DANGER_TEXT : SUCCESS_TEXT,
          )}
        >
          {openSince ? formatElapsed(clock.getTime() - openSince) : "--:--:--"}
        </div>
      </div>
      <div className="pl-[1.6em] border-l border-border text-right">
        <Label>
          {formatDate(clock)} · UTC {moment(clock).utc().format("HH:mm")}
        </Label>
        <div className="font-mono font-medium text-[2.6em] leading-none mt-[0.15em]">
          {formatClock(clock)}
        </div>
      </div>
    </header>
  );
};

const Label = ({
  children,
  className,
}: {
  children: React.ReactNode;
  className?: string;
}) => (
  <div
    className={cn(
      "font-mono text-[0.75em] tracking-[0.16em] uppercase text-muted-foreground",
      className,
    )}
  >
    {children}
  </div>
);

/* =====================================================================
   KPI strip — GO ring, totals and 10-minute telemetry
   ===================================================================== */

interface SummaryShape {
  totalEndpoints: number;
  failingCount: number;
  ackedFailingCount: number;
  unackedFreshFailures: number;
  /** Pending + deferred across the fleet. */
  backlogTotal: number;
  backlogDeltaPerMin: number;
  /** Endpoints whose backlog is at or above BACKLOG_ELEVATED. */
  backlogHotCount: number;
  failedTotal: number;
  /** New failures per hour on unacknowledged endpoints. */
  failedRatePerHour: number;
}

const KpiStrip = ({
  endpoints,
  summary,
  telemetry,
}: {
  endpoints: MonitorEndpoint[];
  summary: SummaryShape;
  telemetry: FleetSample[];
}) => {
  const unacked = summary.failingCount - summary.ackedFailingCount;
  const backlogTrend =
    Math.round(summary.backlogDeltaPerMin) === 0
      ? "stable"
      : `${summary.backlogDeltaPerMin > 0 ? "▲" : "▼"} ${formatBigNumber(Math.abs(Math.round(summary.backlogDeltaPerMin)))} / min`;
  return (
    <section
      aria-label="Fleet summary"
      className={cn(
        "grid rounded-nb-md bg-card border border-border",
        "grid-cols-2 xl:grid-cols-[auto_1fr_1fr_1fr_1.9fr]",
        "[&>*]:px-[1.4em] [&>*]:py-[1.1em] [&>*]:min-w-0",
        "xl:[&>*+*]:border-l xl:[&>*+*]:border-border",
      )}
    >
      <FleetRing endpoints={endpoints} />
      <KpiCell
        label="Endpoints failing"
        swatch="bg-status-danger"
        value={summary.failingCount.toString()}
        unit={`/ ${summary.totalEndpoints}`}
        sub={`${unacked} unacknowledged · ${summary.ackedFailingCount} acked`}
        subClass={unacked > 0 ? DANGER_TEXT : undefined}
      />
      <KpiCell
        label="Backlog"
        swatch="bg-status-warning"
        value={formatBigNumber(summary.backlogTotal)}
        sub={`pending + deferred · ${backlogTrend}`}
        subClass={summary.backlogHotCount > 0 ? WARNING_TEXT : undefined}
      />
      <KpiCell
        label="Failed messages"
        swatch="bg-status-danger"
        value={formatBigNumber(summary.failedTotal)}
        sub={
          summary.failedRatePerHour >= 1
            ? `▲ ${formatBigNumber(Math.round(summary.failedRatePerHour))} / hour`
            : summary.failedTotal > 0
              ? "no new failures"
              : "none uncleared"
        }
        subClass={summary.failedTotal > 0 ? DANGER_TEXT : undefined}
      />
      <TelemetryChart telemetry={telemetry} />
    </section>
  );
};

const KpiCell = ({
  label,
  swatch,
  value,
  unit,
  sub,
  subClass,
}: {
  label: string;
  swatch: string;
  value: string;
  unit?: string;
  sub: string;
  subClass?: string;
}) => (
  <div>
    <Label className="flex items-center gap-[0.6em]">
      <span aria-hidden="true" className={cn("w-[0.7em] h-[0.7em] rounded-[2px]", swatch)} />
      {label}
    </Label>
    <div className="font-mono font-bold text-[3.4em] leading-none tracking-tight mt-[0.15em] tabular-nums">
      {value}
      {unit && (
        <small className="text-[0.42em] font-normal text-muted-foreground ml-[0.3em]">
          {unit}
        </small>
      )}
    </div>
    <div
      className={cn(
        "font-mono text-[0.85em] mt-[0.8em]",
        subClass ?? "text-muted-foreground",
      )}
    >
      {sub}
    </div>
  </div>
);

const RING_STROKE: Record<EndpointState, string> = {
  nogo: "stroke-status-danger",
  acked: "stroke-border-strong",
  hold: "stroke-status-warning",
  go: "stroke-status-success",
};

/** One segment per endpoint, A → Z, so a segment never moves between polls. */
const FleetRing = ({ endpoints }: { endpoints: MonitorEndpoint[] }) => {
  const sorted = [...endpoints].sort((a, b) => a.id.localeCompare(b.id));
  const n = sorted.length;
  const go = sorted.filter((e) => endpointState(e) === "go").length;
  const r = 42;
  const gap = n > 1 ? 3.2 / r : 0;
  const point = (a: number) =>
    `${(50 + r * Math.cos(a)).toFixed(2)} ${(50 + r * Math.sin(a)).toFixed(2)}`;
  return (
    <div className="relative grid place-items-center">
      <svg
        viewBox="0 0 100 100"
        className="w-[8.5em] h-[8.5em]"
        role="img"
        aria-label={`${go} of ${n} endpoints GO`}
      >
        {n <= 1 ? (
          <circle
            cx={50}
            cy={50}
            r={r}
            fill="none"
            strokeWidth={9}
            className={n === 1 ? RING_STROKE[endpointState(sorted[0])] : "stroke-border"}
          />
        ) : (
          sorted.map((e, i) => {
            const a0 = -Math.PI / 2 + (i / n) * 2 * Math.PI + gap / 2;
            const a1 = -Math.PI / 2 + ((i + 1) / n) * 2 * Math.PI - gap / 2;
            return (
              <path
                key={e.id}
                d={`M${point(a0)} A${r} ${r} 0 ${a1 - a0 > Math.PI ? 1 : 0} 1 ${point(a1)}`}
                fill="none"
                strokeWidth={9}
                className={RING_STROKE[endpointState(e)]}
              >
                <title>{e.id}</title>
              </path>
            );
          })
        )}
      </svg>
      <div className="absolute inset-0 grid place-content-center text-center pointer-events-none">
        <span className={cn("text-[2.4em] font-extrabold leading-none", SUCCESS_TEXT)}>
          {go}
        </span>
        <span className="font-mono text-[0.75em] tracking-[0.1em] text-muted-foreground mt-[0.3em]">
          / {n} GO
        </span>
      </div>
    </div>
  );
};

const TELEMETRY_WINDOW_MS = 10 * 60_000;

/**
 * Fleet failed/min and backlog over the last 10 minutes. Samples live in the
 * browser, so after a reload the lines grow in from the right as the page
 * collects history. The two series use independent scales — the legend
 * carries the current values.
 */
const TelemetryChart = ({ telemetry }: { telemetry: FleetSample[] }) => {
  const W = 600;
  const H = 100;
  const last = telemetry[telemetry.length - 1];
  const failedPerMin = failedPerMinuteSeries(telemetry);
  const line = (values: number[]) => {
    const max = Math.max(...values);
    const min = Math.min(...values);
    const range = max - min;
    return values
      .map((v, i) => {
        const x = W - ((last.t - telemetry[i].t) / TELEMETRY_WINDOW_MS) * W;
        const y = range === 0 ? H / 2 : H - 6 - ((v - min) / range) * (H - 12);
        return `${x.toFixed(1)},${y.toFixed(1)}`;
      })
      .join(" ");
  };
  return (
    <div className="grid grid-rows-[auto_1fr_auto] gap-[0.5em] col-span-2 xl:col-span-1">
      <div className="flex flex-wrap justify-between gap-[1em]">
        <Label>Telemetry · last 10 min</Label>
        {last && (
          <div className="flex gap-[1.2em] font-mono text-[0.85em]">
            <span className={DANGER_TEXT}>
              — Failed / min {formatBigNumber(Math.round(failedPerMin[failedPerMin.length - 1]))}
            </span>
            <span className={WARNING_TEXT}>
              — Backlog {formatBigNumber(last.backlog)}
            </span>
          </div>
        )}
      </div>
      <div className="relative min-h-[4.5em]">
        {telemetry.length < 2 ? (
          <div className="absolute inset-0 grid place-content-center font-mono text-[0.85em] text-muted-foreground">
            Collecting samples…
          </div>
        ) : (
          <svg
            viewBox={`0 0 ${W} ${H}`}
            preserveAspectRatio="none"
            className="absolute inset-0 w-full h-full"
            aria-hidden="true"
          >
            {[0, W / 2, W].map((x) => (
              <line
                key={x}
                x1={x}
                x2={x}
                y1={0}
                y2={H}
                className="stroke-border"
                vectorEffect="non-scaling-stroke"
              />
            ))}
            <polyline
              points={line(telemetry.map((s) => s.backlog))}
              fill="none"
              strokeWidth={1.75}
              className="stroke-status-warning"
              vectorEffect="non-scaling-stroke"
            />
            <polyline
              points={line(failedPerMin)}
              fill="none"
              strokeWidth={1.75}
              className="stroke-status-danger"
              vectorEffect="non-scaling-stroke"
            />
          </svg>
        )}
      </div>
      <div className="flex justify-between font-mono text-[0.7em] text-muted-foreground">
        <span>−10 min</span>
        <span>−5 min</span>
        <span>now</span>
      </div>
    </div>
  );
};

/* =====================================================================
   Bands
   ===================================================================== */

const BandHeader = ({
  title,
  meta,
  titleClass,
  metaClass,
}: {
  title: string;
  meta: string;
  titleClass: string;
  metaClass: string;
}) => (
  <div className="flex flex-wrap items-baseline justify-between gap-x-[2em] gap-y-[0.3em]">
    <h3
      className={cn(
        "m-0 flex items-center gap-[0.6em] text-[1.05em] font-extrabold tracking-[0.16em] uppercase",
        titleClass,
      )}
    >
      <span aria-hidden="true" className="w-[0.5em] h-[0.5em] rounded-full bg-current" />
      {title}
    </h3>
    <span className={cn("font-mono text-[0.85em]", metaClass)}>{meta}</span>
  </div>
);

const CARD_GRID = "grid gap-[0.7em] grid-cols-[repeat(auto-fill,minmax(24em,1fr))]";
/** Backlog cards carry one stat fewer and share the row with Healthy. */
const BACKLOG_CARD_GRID = "grid gap-[0.7em] grid-cols-[repeat(auto-fill,minmax(19em,1fr))]";

interface FailingBandProps {
  endpoints: MonitorEndpoint[];
  total: number;
  onAck: (id: string, reason?: string) => void;
  onUnack: (id: string) => void;
  now: number;
}

const FailingBand = ({ endpoints, total, onAck, onUnack, now }: FailingBandProps) => {
  if (total === 0) return null;
  if (endpoints.length === 0) {
    return (
      <section className="rounded-nb-md px-[1.1em] py-[1em] flex flex-col gap-[0.8em] border border-status-success/30 bg-status-success/[0.05]">
        <BandHeader
          title="Failing · none"
          meta="all clear"
          titleClass={SUCCESS_TEXT}
          metaClass={SUCCESS_TEXT}
        />
        <div className="flex flex-wrap items-baseline gap-x-[1.2em] gap-y-[0.4em]">
          <span className={cn("text-[2.6em] font-extrabold tracking-tight", SUCCESS_TEXT)}>
            No failing endpoints
          </span>
          <span className="font-mono text-[0.9em] text-muted-foreground">
            {total} endpoint{total === 1 ? "" : "s"} · 0 failed messages
          </span>
        </div>
      </section>
    );
  }
  const unacked = endpoints.filter((e) => !e.ack).length;
  return (
    <section className="rounded-nb-md px-[1.1em] py-[1em] flex flex-col gap-[0.8em] border border-status-danger/40 bg-status-danger/[0.06]">
      <BandHeader
        title="Failing · sorted by impact"
        meta={`${unacked} unacknowledged · click ACK to silence`}
        titleClass={DANGER_TEXT}
        metaClass={DANGER_TEXT}
      />
      <div className={CARD_GRID}>
        {endpoints.map((e) => (
          <FailingCard
            key={e.id}
            endpoint={e}
            now={now}
            onAck={onAck}
            onUnack={onUnack}
          />
        ))}
      </div>
    </section>
  );
};

const WatchingBand = ({
  endpoints,
  lastRefreshAt,
  now,
}: {
  endpoints: MonitorEndpoint[];
  lastRefreshAt: number | undefined;
  now: number;
}) => {
  if (endpoints.length === 0) return null;
  const hot = endpoints.filter((e) => backlogLevel(e) !== "normal").length;
  return (
    <section className="rounded-nb-md px-[1.1em] py-[1em] flex flex-col gap-[0.8em] border border-status-warning/40 bg-status-warning/[0.05]">
      <BandHeader
        title="Backlog · sorted by depth"
        meta={
          hot > 0
            ? `${hot} endpoint${hot === 1 ? "" : "s"} at or above ${BACKLOG_ELEVATED} pending`
            : `all below ${BACKLOG_ELEVATED} pending`
        }
        titleClass={WARNING_TEXT}
        metaClass={hot > 0 ? WARNING_TEXT : "text-muted-foreground"}
      />
      <div className={BACKLOG_CARD_GRID}>
        {endpoints.map((e) => (
          <BacklogCard
            key={e.id}
            endpoint={e}
            lastRefreshAt={lastRefreshAt}
            now={now}
          />
        ))}
      </div>
    </section>
  );
};

const HealthyStrip = ({ endpoints }: { endpoints: MonitorEndpoint[] }) => {
  if (endpoints.length === 0) return null;
  return (
    <section className="rounded-nb-md px-[1.1em] py-[1em] flex flex-col gap-[0.8em] border border-status-success/30 bg-status-success/[0.04]">
      <BandHeader
        title={`Healthy · ${endpoints.length}`}
        meta="A → Z"
        titleClass={SUCCESS_TEXT}
        metaClass={SUCCESS_TEXT}
      />
      <div className="grid gap-[0.45em] grid-cols-[repeat(auto-fill,minmax(13em,1fr))]">
        {endpoints.map((e) => (
          <div
            key={e.id}
            title={e.id}
            className="rounded-nb-sm bg-card border border-border border-l-[3px] border-l-status-success px-[0.75em] py-[0.55em] flex items-center gap-[0.55em] min-w-0"
          >
            <span className="w-[0.5em] h-[0.5em] rounded-full bg-status-success shrink-0 nb-monitor-pulseg" />
            <span className="truncate font-semibold text-[1em]">{e.id}</span>
          </div>
        ))}
      </div>
    </section>
  );
};

/* =====================================================================
   Endpoint cards
   ===================================================================== */

interface FailingCardProps {
  endpoint: MonitorEndpoint;
  now: number;
  onAck: (id: string, reason?: string) => void;
  onUnack: (id: string) => void;
}

const FailingCard = ({ endpoint, now, onAck, onUnack }: FailingCardProps) => {
  const acked = Boolean(endpoint.ack);
  // A failing endpoint can also be drowning in queued work — worth knowing
  // before you ack the failure — so a deep backlog turns its queue amber.
  const queueClass =
    backlogLevel(endpoint) === "normal" ? undefined : WARNING_TEXT;
  // The pulse is what makes the wall ask for attention. We only flash a
  // card during its first 60 s of newness *and* only if no operator has
  // already silenced it.
  const pulse = endpoint.isFreshFailure && !acked;
  const rising = (endpoint.ratePerMin ?? 0) > 0;

  const sinceText = (() => {
    if (endpoint.ack) {
      const reason = endpoint.ack.reason
        ? ` · "${truncate(endpoint.ack.reason, 24)}"`
        : "";
      return `Acked ${formatDuration(now - endpoint.ack.ackedAt)} ago${reason} · clears on recovery or after 4 h`;
    }
    // The API has no failure start time — `firstFailureAt` is when this page
    // first saw the endpoint failing, so say "seen" rather than "since".
    if (endpoint.firstFailureAt) {
      return `First seen ${formatDuration(now - endpoint.firstFailureAt)} ago`;
    }
    return "—";
  })();

  return (
    <article
      className={cn(
        "rounded-nb-md border-[1.5px] px-[1em] pt-[0.9em] pb-[0.8em] flex flex-col gap-[0.6em] min-w-0",
        acked
          ? "bg-card border-dashed border-border-strong"
          : "bg-status-danger/[0.10] border-status-danger",
        pulse && "nb-monitor-pulse-card",
      )}
    >
      <div className="flex items-center gap-[0.55em] min-w-0">
        <StatusBadge className={acked ? "bg-muted text-muted-foreground" : "bg-status-danger text-white"}>
          !
        </StatusBadge>
        <span className={cn("flex-1 min-w-0 truncate font-bold text-[1.1em]", acked && "text-muted-foreground")}>
          {endpoint.id}
        </span>
        {pulse && (
          <span className="font-mono text-[0.7em] tracking-[0.14em] uppercase font-bold px-[0.7em] py-[0.35em] rounded-nb-sm bg-status-danger text-white">
            new
          </span>
        )}
        <AckButton
          acked={acked}
          onAck={() => onAck(endpoint.id)}
          onUnack={() => onUnack(endpoint.id)}
        />
      </div>

      <HeroNumber
        value={endpoint.status.failedCount ?? 0}
        unit="failed"
        valueClass={acked ? "text-muted-foreground" : undefined}
        unitClass={acked ? "text-muted-foreground" : DANGER_TEXT}
      />

      <dl className="grid grid-cols-4 gap-[0.4em] m-0">
        <Stat
          label="Pending"
          value={endpoint.status.pendingCount ?? 0}
          valueClass={queueClass}
        />
        <Stat
          label="Deferred"
          value={endpoint.status.deferredCount ?? 0}
          valueClass={queueClass}
        />
        <Stat label="incl. DLQ" value={endpoint.status.deadletterCount ?? 0} />
        <Stat
          label="Rate / min"
          value={describeRate(endpoint.ratePerMin)}
          valueClass={rising && !acked ? DANGER_TEXT : undefined}
        />
      </dl>

      <CardFoot
        text={sinceText}
        textClass={acked ? "text-muted-foreground" : cn(DANGER_TEXT, "font-bold")}
        borderClass={acked ? "border-border" : "border-status-danger/30"}
        values={endpoint.samples.map((s) => s.failed)}
        sparkClass={acked ? "text-muted-foreground" : DANGER_TEXT}
      />
    </article>
  );
};

const BacklogCard = ({
  endpoint,
  lastRefreshAt,
  now,
}: {
  endpoint: MonitorEndpoint;
  lastRefreshAt: number | undefined;
  now: number;
}) => {
  const level = backlogLevel(endpoint);
  const trend = pendingPerMin(endpoint);
  const eta = describeDrainEta(endpoint) ?? "—";
  return (
    <article
      className={cn(
        "rounded-nb-md px-[1em] pt-[0.9em] pb-[0.8em] flex flex-col gap-[0.6em] min-w-0",
        "bg-status-warning/[0.08]",
        level === "high"
          ? "border-[3px] border-status-warning"
          : level === "elevated"
            ? "border-[1.5px] border-status-warning"
            : "border-[1.5px] border-status-warning/40",
      )}
    >
      <div className="flex items-center gap-[0.55em] min-w-0">
        <StatusBadge className="bg-status-warning text-ink">◷</StatusBadge>
        <span className="flex-1 min-w-0 truncate font-bold text-[1.1em]">
          {endpoint.id}
        </span>
        {level !== "normal" && <BacklogTag level={level} />}
      </div>

      <HeroNumber
        value={endpoint.status.pendingCount ?? 0}
        unit="pending"
        unitClass={WARNING_TEXT}
      />

      <dl className="grid grid-cols-3 gap-[0.4em] m-0">
        <Stat label="Deferred" value={endpoint.status.deferredCount ?? 0} />
        <Stat
          label="Trend / min"
          value={describeRate(trend)}
          valueClass={
            trend === undefined || Math.round(trend) === 0
              ? undefined
              : trend > 0
                ? WARNING_TEXT
                : SUCCESS_TEXT
          }
        />
        <Stat
          label="Drain ETA"
          value={eta}
          valueClass={eta === "growing" ? WARNING_TEXT : undefined}
        />
      </dl>

      <CardFoot
        text={
          lastRefreshAt
            ? `Updated ${Math.max(0, Math.round((now - lastRefreshAt) / 1000))}s ago`
            : "—"
        }
        textClass={WARNING_TEXT}
        borderClass="border-status-warning/30"
        values={endpoint.samples.map((s) => s.pending)}
        sparkClass={WARNING_TEXT}
      />
    </article>
  );
};

const StatusBadge = ({
  className,
  children,
}: {
  className: string;
  children: React.ReactNode;
}) => (
  <span
    aria-hidden="true"
    className={cn(
      "w-[1.5em] h-[1.5em] rounded-full inline-flex items-center justify-center shrink-0",
      "font-mono font-extrabold text-[0.8em]",
      className,
    )}
  >
    {children}
  </span>
);

const HeroNumber = ({
  value,
  unit,
  valueClass,
  unitClass,
}: {
  value: number;
  unit: string;
  valueClass?: string;
  unitClass: string;
}) => (
  <div className="flex items-baseline gap-[0.5em]">
    <span
      className={cn(
        "font-mono font-bold text-[2.8em] leading-none tracking-tight tabular-nums",
        valueClass,
      )}
    >
      {formatBigNumber(value)}
    </span>
    <span
      className={cn(
        "font-mono text-[0.75em] tracking-[0.16em] uppercase font-bold",
        unitClass,
      )}
    >
      {unit}
    </span>
  </div>
);

const Stat = ({
  label,
  value,
  valueClass,
}: {
  label: string;
  value: number | string;
  valueClass?: string;
}) => (
  <div className="min-w-0">
    <dt className="font-mono text-[0.72em] text-muted-foreground whitespace-nowrap">
      {label}
    </dt>
    <dd className={cn("m-0 mt-[0.25em] font-mono font-bold text-[1.05em] whitespace-nowrap tabular-nums", valueClass)}>
      {typeof value === "number" ? formatBigNumber(value) : value}
    </dd>
  </div>
);

const CardFoot = ({
  text,
  textClass,
  borderClass,
  values,
  sparkClass,
}: {
  text: string;
  textClass: string;
  borderClass: string;
  values: number[];
  sparkClass: string;
}) => (
  <div
    className={cn(
      "flex items-center justify-between gap-[1em] pt-[0.6em] border-t font-mono text-[0.78em]",
      borderClass,
    )}
  >
    <span className={cn("min-w-0", textClass)}>{text}</span>
    <Sparkline values={values} className={sparkClass} />
  </div>
);

/** Pill that calls out a backlog card sitting on a deep queue. */
const BacklogTag = ({ level }: { level: BacklogLevel }) => (
  <span
    className={cn(
      "font-mono text-[0.7em] tracking-[0.14em] uppercase font-bold shrink-0",
      "px-[0.7em] py-[0.35em] rounded-nb-sm border",
      level === "high"
        ? "bg-status-warning text-ink border-status-warning"
        : cn("bg-status-warning/[0.14] border-status-warning/50", WARNING_TEXT),
    )}
    title={
      level === "high"
        ? `Backlog at or above ${BACKLOG_HIGH} pending, or growing while already elevated`
        : `Backlog at or above ${BACKLOG_ELEVATED} pending`
    }
  >
    {level}
  </span>
);

const AckButton = ({
  acked,
  onAck,
  onUnack,
}: {
  acked: boolean;
  onAck: () => void;
  onUnack: () => void;
}) => (
  <button
    type="button"
    onClick={(e) => {
      e.preventDefault();
      acked ? onUnack() : onAck();
    }}
    className={cn(
      "font-mono text-[0.7em] tracking-[0.14em] uppercase font-bold shrink-0",
      "px-[0.7em] py-[0.35em] rounded-nb-sm border",
      acked
        ? cn("bg-status-success/[0.14] border-status-success/40", SUCCESS_TEXT)
        : "bg-transparent text-muted-foreground border-border-strong hover:text-foreground hover:border-foreground/40",
      "transition-colors cursor-pointer",
      "focus-visible:outline focus-visible:outline-2 focus-visible:outline-primary focus-visible:outline-offset-2",
    )}
    title={acked ? "Click to clear acknowledgement" : "Acknowledge this failure (auto-expires after 4 h or on recovery)"}
  >
    {acked ? "✓ acked" : "ack"}
  </button>
);

/* =====================================================================
   Sparkline + visual primitives
   ===================================================================== */

/** Flat line, no fill; the stroke follows `currentColor`. */
const Sparkline = ({ values, className }: { values: number[]; className: string }) => {
  const W = 100;
  const H = 24;
  const min = Math.min(...values);
  const max = Math.max(...values);
  const range = max - min;
  const points =
    values.length < 2
      ? `0,${H / 2} ${W},${H / 2}`
      : values
          .map((v, i) => {
            // Higher = "worse" → draw at the top of the box.
            const y = range === 0 ? H / 2 : H - 2 - ((v - min) / range) * (H - 4);
            return `${((i / (values.length - 1)) * W).toFixed(1)},${y.toFixed(1)}`;
          })
          .join(" ");
  return (
    <svg
      viewBox={`0 0 ${W} ${H}`}
      preserveAspectRatio="none"
      className={cn("w-[38%] h-[1.5em] shrink-0", className)}
      aria-hidden="true"
    >
      <polyline
        points={points}
        fill="none"
        stroke="currentColor"
        strokeWidth={1.75}
        strokeLinejoin="round"
        vectorEffect="non-scaling-stroke"
      />
    </svg>
  );
};

const Pulser = () => (
  <span
    aria-hidden="true"
    className="inline-block w-[0.6em] h-[0.6em] rounded-full bg-current nb-monitor-pulser"
  />
);

const LogoMark = () => (
  <svg width="1em" height="1em" viewBox="0 0 36 36" fill="none" aria-hidden="true">
    <path
      d="M5 22c0-3.5 2.8-6.3 6.3-6.3.7 0 1.4.1 2 .3.8-3.7 4.1-6.5 8-6.5 4.2 0 7.6 3.2 8.1 7.2 2.7.4 4.8 2.7 4.8 5.5 0 3.1-2.5 5.5-5.5 5.5H11.3C7.8 27.7 5 25 5 22z"
      stroke="currentColor"
      strokeWidth={2.5}
      strokeLinejoin="round"
    />
    <circle cx={29} cy={11} r={3} fill="#E8743C" />
  </svg>
);

/* =====================================================================
   Footer (legend + ticker + heartbeat) and stale banner
   ===================================================================== */

const WallFoot = ({
  ticker,
  lastRefreshAt,
  isStale,
  now,
}: {
  ticker: TickerEvent[];
  lastRefreshAt: number | undefined;
  isStale: boolean;
  now: number;
}) => {
  const seconds = lastRefreshAt
    ? Math.max(0, Math.round((now - lastRefreshAt) / 1000))
    : undefined;
  return (
    <footer className="flex flex-wrap items-center gap-x-[1.6em] gap-y-[0.5em] font-mono text-[0.85em] text-muted-foreground">
      <div className="flex gap-[1.2em] items-center">
        <LegendKey color="bg-status-danger">Failed</LegendKey>
        <LegendKey color="bg-status-warning">Pending / backlog</LegendKey>
        <LegendKey color="bg-status-success">Healthy</LegendKey>
        <LegendKey color="bg-border-strong">Idle / acked</LegendKey>
      </div>
      <Ticker events={ticker} />
      <span className="inline-flex items-center gap-[0.5em] pl-[1.6em] border-l border-border">
        <span
          aria-hidden="true"
          className={cn(
            "w-[0.55em] h-[0.55em] rounded-full",
            isStale ? "bg-status-danger" : "bg-status-success",
          )}
        />
        {seconds === undefined
          ? "Waiting for first update"
          : isStale
            ? `No update for ${seconds}s`
            : `Updated ${seconds}s ago · every 5 s`}
      </span>
    </footer>
  );
};

const LegendKey = ({
  color,
  children,
}: {
  color: string;
  children: React.ReactNode;
}) => (
  <span className="inline-flex items-center gap-[0.45em]">
    <span aria-hidden="true" className={cn("w-[0.75em] h-[0.75em] rounded-[2px]", color)} />
    {children}
  </span>
);

const Ticker = ({ events }: { events: TickerEvent[] }) => {
  // The ticker is the only thing on the wall that *moves* in the absence of
  // incidents. That's intentional — it also doubles as a liveness signal,
  // proving the page hasn't frozen. Pause animation on hover for legibility.
  if (events.length === 0) {
    return (
      <div className="flex-1 min-w-0 overflow-hidden whitespace-nowrap pl-[1.6em] border-l border-border text-muted-foreground/70">
        Waiting for activity…
      </div>
    );
  }
  return (
    <div className="flex-1 min-w-0 overflow-hidden pl-[1.6em] border-l border-border">
      <div className="nb-monitor-ticker whitespace-nowrap">
        {events.map((e) => (
          <TickerItem key={e.id} event={e} />
        ))}
        {/* Duplicate the run so the marquee loop is seamless. */}
        {events.map((e) => (
          <TickerItem key={`${e.id}-dup`} event={e} />
        ))}
      </div>
    </div>
  );
};

const TickerItem = ({ event }: { event: TickerEvent }) => {
  const ts = moment(event.t).format("HH:mm:ss");
  const labelCls =
    event.kind === "failure"
      ? DANGER_TEXT
      : event.kind === "recovery"
        ? SUCCESS_TEXT
        : "text-foreground";
  const verb =
    event.kind === "failure"
      ? "failed"
      : event.kind === "recovery"
        ? "recovered"
        : event.kind === "ack"
          ? "acked"
          : "ack cleared";
  return (
    <span className="inline-block mr-[3em]">
      <span className="text-muted-foreground/70 mr-[0.6em]">{ts}</span>
      <span className={cn("font-semibold", labelCls)}>{event.endpoint}</span>
      <span className="ml-[0.4em]">
        {verb}
        {event.detail ? ` · ${event.detail}` : ""}
      </span>
    </span>
  );
};

const StaleBanner = ({
  lastRefreshAt,
  now,
}: {
  lastRefreshAt: number | undefined;
  now: number;
}) => {
  const seconds = lastRefreshAt ? Math.round((now - lastRefreshAt) / 1000) : 0;
  const lastTs = lastRefreshAt
    ? moment(lastRefreshAt).format("HH:mm:ss")
    : "never";
  return (
    <div
      className={cn(
        "rounded-nb-md px-[1.2em] py-[0.8em] flex items-center gap-[0.8em]",
        "bg-status-danger/20 border border-status-danger/60",
        "font-mono text-[1em] tracking-wider",
        DANGER_TEXT,
      )}
      role="alert"
    >
      <Pulser />
      <span className="font-bold uppercase">Connection lost</span>
      <span>
        Last update {lastTs} · {seconds}s ago · retrying every 5 s
      </span>
    </div>
  );
};

/* =====================================================================
   Pure helpers — splitting bands, summarising, formatting
   ===================================================================== */

interface Bands {
  failing: MonitorEndpoint[];
  watching: MonitorEndpoint[];
  healthy: MonitorEndpoint[];
}

function splitIntoBands(endpoints: MonitorEndpoint[]): Bands {
  const failing: MonitorEndpoint[] = [];
  const watching: MonitorEndpoint[] = [];
  const healthy: MonitorEndpoint[] = [];
  for (const e of endpoints) {
    const state = endpointState(e);
    if (state === "nogo" || state === "acked") {
      failing.push(e);
    } else if (state === "hold") {
      watching.push(e);
    } else {
      healthy.push(e);
    }
  }
  // Failing: unacked first, then by impact descending. The 14k-failed card
  // should land top-left and tiny incidents settle at the end of the band.
  failing.sort((a, b) => {
    const aAck = a.ack ? 1 : 0;
    const bAck = b.ack ? 1 : 0;
    if (aAck !== bAck) return aAck - bAck;
    return (b.status.failedCount ?? 0) - (a.status.failedCount ?? 0);
  });
  // Watching: largest backlog first.
  watching.sort((a, b) => backlogTotal(b) - backlogTotal(a));
  // Healthy: alphabetical so chip placement is stable across refreshes —
  // a chip jumping around because traffic ticked up is a visual distraction
  // on the wall.
  healthy.sort((a, b) => a.id.localeCompare(b.id));
  return { failing, watching, healthy };
}

/**
 * NO-GO: failing and not acknowledged. ACKED: failing but silenced. HOLD: no
 * failures, but messages queued. GO: nothing failed or queued.
 */
export function endpointState(endpoint: MonitorEndpoint): EndpointState {
  if ((endpoint.status.failedCount ?? 0) > 0) {
    return endpoint.ack ? "acked" : "nogo";
  }
  return backlogTotal(endpoint) > 0 ? "hold" : "go";
}

/**
 * When the page first saw the oldest still-open (unacknowledged) failure —
 * drives the header's T+ clock. Undefined when nothing is failing unacked.
 */
export function firstOpenFailureAt(
  endpoints: MonitorEndpoint[],
): number | undefined {
  let first: number | undefined;
  for (const e of endpoints) {
    if (endpointState(e) !== "nogo" || e.firstFailureAt === undefined) continue;
    if (first === undefined || e.firstFailureAt < first) first = e.firstFailureAt;
  }
  return first;
}

/**
 * Converts cumulative fleet failed totals into new failures per minute,
 * measured over the trailing minute of samples. Resubmits and skips shrink
 * the total; those read as zero rather than as negative failures.
 */
export function failedPerMinuteSeries(telemetry: FleetSample[]): number[] {
  let start = 0;
  return telemetry.map((sample, i) => {
    while (start < i && sample.t - telemetry[start].t > 60_000) start += 1;
    const from = telemetry[start];
    const dt = sample.t - from.t;
    if (dt <= 0) return 0;
    return Math.max(0, ((sample.failed - from.failed) / dt) * 60_000);
  });
}

/** Pending + deferred — everything queued up in front of an endpoint. */
export function backlogTotal(endpoint: MonitorEndpoint): number {
  return (
    (endpoint.status.pendingCount ?? 0) + (endpoint.status.deferredCount ?? 0)
  );
}

/**
 * How loudly the wall should call out an endpoint's queue depth.
 *
 * Absolute thresholds keep the answer predictable from across a room — the
 * same number always earns the same colour, whatever the rest of the wall is
 * doing. Growth only escalates an already-notable backlog: a handful of
 * messages ticking up is normal in-flight traffic, but a queue that is past
 * BACKLOG_ELEVATED *and* still climbing is worse than its number suggests.
 */
export function backlogLevel(endpoint: MonitorEndpoint): BacklogLevel {
  const total = backlogTotal(endpoint);
  if (total >= BACKLOG_HIGH) return "high";
  if (total < BACKLOG_ELEVATED) return "normal";
  return isBacklogGrowing(endpoint) ? "high" : "elevated";
}

function isBacklogGrowing(endpoint: MonitorEndpoint): boolean {
  if (endpoint.samples.length < 2) return false;
  const oldest = endpoint.samples[0];
  const latest = endpoint.samples[endpoint.samples.length - 1];
  return (
    latest.pending + latest.deferred > oldest.pending + oldest.deferred
  );
}

/** Pending messages gained (or drained, when negative) per minute. */
function pendingPerMin(endpoint: MonitorEndpoint): number | undefined {
  if (endpoint.samples.length < 2) return undefined;
  const oldest = endpoint.samples[0];
  const latest = endpoint.samples[endpoint.samples.length - 1];
  const dt = latest.t - oldest.t;
  if (dt <= 0) return undefined;
  return ((latest.pending - oldest.pending) / dt) * 60_000;
}

function summarize(endpoints: MonitorEndpoint[]): SummaryShape {
  const summary: SummaryShape = {
    totalEndpoints: endpoints.length,
    failingCount: 0,
    ackedFailingCount: 0,
    unackedFreshFailures: 0,
    backlogTotal: 0,
    backlogDeltaPerMin: 0,
    backlogHotCount: 0,
    failedTotal: 0,
    failedRatePerHour: 0,
  };

  for (const e of endpoints) {
    const failed = e.status.failedCount ?? 0;
    summary.failedTotal += failed;
    summary.backlogTotal += backlogTotal(e);
    if (backlogLevel(e) !== "normal") summary.backlogHotCount += 1;
    if (failed > 0) {
      summary.failingCount += 1;
      if (e.ack) {
        summary.ackedFailingCount += 1;
      } else {
        if (e.isFreshFailure) summary.unackedFreshFailures += 1;
        // Acked endpoints are known problems — keep them out of the rate.
        if (e.ratePerMin && e.ratePerMin > 0) {
          summary.failedRatePerHour += e.ratePerMin * 60;
        }
      }
    }
    // Backlog rate uses the same sample window we keep for the sparkline.
    if (e.samples.length >= 2) {
      const oldest = e.samples[0];
      const latest = e.samples[e.samples.length - 1];
      const dt = latest.t - oldest.t;
      if (dt > 0) {
        summary.backlogDeltaPerMin +=
          ((latest.pending + latest.deferred - oldest.pending - oldest.deferred) /
            dt) *
          60_000;
      }
    }
  }

  return summary;
}

function describeRate(rate: number | undefined): string {
  if (rate === undefined) return "—";
  const rounded = Math.round(rate);
  if (rounded === 0) return "stable";
  return rounded > 0
    ? `▲ ${formatBigNumber(rounded)}`
    : `▼ ${formatBigNumber(Math.abs(rounded))}`;
}

function describeDrainEta(endpoint: MonitorEndpoint): string | undefined {
  // ETA = pending / drain-rate-per-min. We estimate the drain rate from the
  // sample history: if pending is decreasing, the slope is our drain rate.
  // If it's flat or rising, we can't promise an ETA — surface "—" instead of
  // lying. Better silent than wrong on a wall display.
  const perMin = pendingPerMin(endpoint);
  if (perMin === undefined) return undefined;
  const pending = endpoint.status.pendingCount ?? 0;
  // Round like the Trend stat so the two never disagree about a flat queue.
  const drainPerMin = -Math.round(perMin);
  if (drainPerMin < 0) return pending > 0 ? "growing" : "—";
  if (drainPerMin === 0) return "—";
  const minutes = pending / drainPerMin;
  if (minutes < 1) return "< 1 min";
  if (minutes < 60) return `~ ${Math.round(minutes)} min`;
  const hours = minutes / 60;
  if (hours < 24) return `~ ${hours.toFixed(1)} h`;
  return `~ ${Math.round(hours / 24)} d`;
}

function formatBigNumber(n: number): string {
  if (!Number.isFinite(n)) return "—";
  const abs = Math.abs(n);
  if (abs >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (abs >= 10_000) return Math.round(n).toLocaleString();
  return n.toLocaleString();
}

function formatClock(d: Date): string {
  const hh = d.getHours().toString().padStart(2, "0");
  const mm = d.getMinutes().toString().padStart(2, "0");
  const ss = d.getSeconds().toString().padStart(2, "0");
  return `${hh}:${mm}:${ss}`;
}

function formatDate(d: Date): string {
  return moment(d).format("ddd · DD MMM YYYY");
}

/** HH:MM:SS elapsed, for the T+ clock. */
function formatElapsed(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000));
  return [Math.floor(s / 3600), Math.floor((s % 3600) / 60), s % 60]
    .map((part) => part.toString().padStart(2, "0"))
    .join(":");
}

/** Compact duration: "16s", "52m 52s", "2h 04m". */
function formatDuration(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000));
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${(s % 60).toString().padStart(2, "0")}s`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h ${(m % 60).toString().padStart(2, "0")}m`;
  return `${Math.floor(h / 24)}d ${(h % 24).toString().padStart(2, "0")}h`;
}

function truncate(s: string, n: number): string {
  return s.length > n ? `${s.slice(0, n - 1)}…` : s;
}
