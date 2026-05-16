import { useEffect, useMemo, useState } from "react";
import moment from "moment";
import { cn } from "lib/utils";
import { getEnv, getApplicationStatus } from "hooks/app-status";
import {
  useMonitorData,
  type MonitorEndpoint,
  type TickerEvent,
} from "hooks/use-monitor-data";

/**
 * Live Status Monitor — designed for a wall display, not a desktop tool.
 *
 * Implements the design in `docs/.cache-design/monitor-ux.html`:
 *  - 4-tile KPI mega-banner answering "are we okay?" at a glance
 *  - Failing band sorted by impact, with hero numbers + ACK
 *  - Watching band for amber pending/deferred (≠ failed)
 *  - Healthy chip-strip so green endpoints don't burn pixels
 *  - Footer ticker (legend + scrolling recent events) as a liveness signal
 *  - Stale-data banner if auto-refresh dies — the worst monitoring failure
 *    is a frozen page that looks healthy.
 *
 * Replaces the old `/Monitor` view (MVC `_Layout.cshtml` link still exists
 * for backwards compatibility; this React page is the new landing).
 */
export default function Monitor() {
  const data = useMonitorData();
  const envFromHook = getEnv();
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
  const env = envFromHook ?? "dev";

  return (
    <div
      className={cn(
        "min-h-screen flex flex-col p-6 gap-4",
        "bg-[#0B0A07] text-[#F4F2EA] font-sans",
        "[font-feature-settings:'tnum']",
        showCursor ? "" : "cursor-none",
      )}
    >
      {data.isStale && (
        <StaleBanner lastRefreshAt={data.lastRefreshAt} now={now.getTime()} />
      )}
      <div
        className={cn(
          "flex flex-col gap-4 transition-opacity",
          data.isStale ? "opacity-70" : "opacity-100",
        )}
      >
        <Hero
          env={env}
          tenant={tenantLabel}
          clock={now}
          unackedFresh={summary.unackedFreshFailures}
        />
        <KpiMega summary={summary} />
        <FailingBand
          endpoints={bands.failing}
          onAck={data.ack}
          onUnack={data.unack}
          now={now.getTime()}
        />
        <WatchingBand
          endpoints={bands.watching}
          onAck={data.ack}
          onUnack={data.unack}
          now={now.getTime()}
        />
        <HealthyStrip endpoints={bands.healthy} />
        <WallFoot ticker={data.ticker} />
      </div>
    </div>
  );
}

/* =====================================================================
   Layout sections
   ===================================================================== */

interface HeroProps {
  env: string;
  tenant?: string;
  clock: Date;
  unackedFresh: number;
}

const Hero = ({ env, tenant, clock, unackedFresh }: HeroProps) => {
  const upperEnv = env.toUpperCase();
  // PROD environments get a red-tinted pill because that's the room's most
  // load-bearing question after "is anything broken?" — anything else just
  // borrows the warning amber so it still reads as "this is not prod".
  const envClass =
    upperEnv === "PROD" || upperEnv === "PRD"
      ? "bg-status-danger/[0.22] text-[#E4A096] border-status-danger/40"
      : "bg-status-warning/[0.18] text-[#E7C783] border-status-warning/40";

  return (
    <div
      className={cn(
        "grid grid-cols-[auto_1fr_auto_auto] gap-6 items-center",
        "px-5 py-4 rounded-nb-md",
        "bg-gradient-to-r from-[#1A1814] to-[#15130E]",
        "border border-[#2A2620]",
      )}
    >
      <div className="inline-flex items-baseline gap-1 text-[#F4F2EA] font-extrabold text-[22px] tracking-tight">
        <LogoMark size={22} />
        <span className="ml-1">NimBus</span>
        <span className="text-primary">.</span>
      </div>
      <div className="flex flex-col gap-1 min-w-0">
        <span className="text-[18px] font-bold tracking-tight truncate">
          Live Status Monitor
        </span>
        <span className="font-mono text-[11px] text-[#8A8473] tracking-wider truncate">
          {tenant ? `${tenant} · ` : ""}Auto-refresh every 5 s · Acknowledge to silence
          {unackedFresh > 0
            ? ` · ${unackedFresh} new failure${unackedFresh === 1 ? "" : "s"}`
            : ""}
        </span>
      </div>
      <span
        className={cn(
          "font-mono text-[11px] tracking-[0.16em] uppercase font-bold",
          "px-2.5 py-1 rounded-nb-sm border",
          "inline-flex items-center gap-1.5",
          envClass,
        )}
      >
        <Pulser />
        {upperEnv}
      </span>
      <div className="flex flex-col items-end leading-none font-mono">
        <span className="text-[32px] font-bold tracking-wide tabular-nums">
          {formatClock(clock)}
        </span>
        <span className="text-[11px] text-[#8A8473] tracking-[0.16em] uppercase mt-1 font-medium">
          {formatDate(clock)}
        </span>
      </div>
    </div>
  );
};

interface SummaryShape {
  totalEndpoints: number;
  failingCount: number;
  ackedFailingCount: number;
  freshFailuresLast5m: number;
  unackedFreshFailures: number;
  pendingTotal: number;
  pendingDeltaPerMin: number;
  failedTotal: number;
  failedRatePerHour: number;
  healthyCount: number;
}

const KpiMega = ({ summary }: { summary: SummaryShape }) => (
  <div className="grid grid-cols-[1.6fr_1fr_1fr_1fr] gap-3">
    <KpiTile
      tone={summary.failingCount > 0 ? "bad" : "ok"}
      icon={summary.failingCount > 0 ? "⚠" : "✓"}
      label="Endpoints failing"
      value={summary.failingCount.toString()}
      unit={` / ${summary.totalEndpoints}`}
      delta={
        summary.failingCount === 0
          ? "all clear"
          : `${summary.freshFailuresLast5m > 0 ? "▲ " : ""}${summary.freshFailuresLast5m} in last 5 min · ${summary.ackedFailingCount} acknowledged`
      }
    />
    <KpiTile
      tone={summary.pendingTotal > 0 ? "warn" : "muted"}
      icon="⏱"
      label="Pending"
      value={formatBigNumber(summary.pendingTotal)}
      delta={
        summary.pendingDeltaPerMin === 0
          ? "stable"
          : `${summary.pendingDeltaPerMin > 0 ? "▲" : "▼"} ${Math.abs(Math.round(summary.pendingDeltaPerMin))} / min`
      }
    />
    <KpiTile
      tone={summary.failedTotal > 0 ? "bad" : "muted"}
      icon="!"
      label="Failed (cumulative)"
      value={formatBigNumber(summary.failedTotal)}
      delta={
        summary.failedRatePerHour > 0
          ? `▲ ${Math.round(summary.failedRatePerHour)} / h`
          : "no new failures"
      }
    />
    <KpiTile
      tone="ok"
      icon="✓"
      label="Healthy"
      value={summary.healthyCount.toString()}
      delta="all clear"
    />
  </div>
);

type KpiTone = "bad" | "warn" | "ok" | "muted";

interface KpiTileProps {
  tone: KpiTone;
  icon: string;
  label: string;
  value: string;
  unit?: string;
  delta?: string;
}

const KpiTile = ({ tone, icon, label, value, unit, delta }: KpiTileProps) => {
  const toneCls: Record<KpiTone, string> = {
    bad: "border-status-danger/50 bg-gradient-to-r from-status-danger/[0.18] to-[#15130E]",
    warn: "border-status-warning/40 bg-gradient-to-r from-status-warning/[0.12] to-[#15130E]",
    ok: "border-status-success/40 bg-[#15130E]",
    muted: "border-[#2A2620] bg-[#15130E]",
  };
  const iconCls: Record<KpiTone, string> = {
    bad: "bg-status-danger/[0.28] text-[#E4A096]",
    warn: "bg-status-warning/[0.22] text-[#E7C783]",
    ok: "bg-status-success/[0.22] text-[#7FD2A6]",
    muted: "bg-[#23201A] text-[#8A8473]",
  };
  const deltaCls: Record<KpiTone, string> = {
    bad: "text-[#E4A096]",
    warn: "text-[#E7C783]",
    ok: "text-[#8A8473]",
    muted: "text-[#8A8473]",
  };
  return (
    <div
      className={cn(
        "rounded-nb-md border px-5 py-4 flex items-center gap-4 overflow-hidden",
        toneCls[tone],
      )}
    >
      <span
        className={cn(
          "w-[42px] h-[42px] rounded-md font-mono font-extrabold text-lg",
          "inline-flex items-center justify-center shrink-0",
          iconCls[tone],
        )}
      >
        {icon}
      </span>
      <div className="min-w-0">
        <div className="font-mono text-[10px] tracking-[0.14em] uppercase text-[#8A8473] font-semibold mb-1">
          {label}
        </div>
        <div className="text-[30px] font-extrabold leading-none tracking-tight tabular-nums">
          {value}
          {unit && (
            <small className="text-[13px] font-medium text-[#8A8473] ml-1.5">
              {unit}
            </small>
          )}
        </div>
        {delta && (
          <div className={cn("font-mono text-[10.5px] mt-1.5", deltaCls[tone])}>
            {delta}
          </div>
        )}
      </div>
    </div>
  );
};

interface BandProps {
  endpoints: MonitorEndpoint[];
  onAck: (id: string, reason?: string) => void;
  onUnack: (id: string) => void;
  now: number;
}

const FailingBand = ({ endpoints, onAck, onUnack, now }: BandProps) => {
  if (endpoints.length === 0) return null;
  const freshUnacked = endpoints.filter(
    (e) => e.isFreshFailure && !e.ack,
  ).length;
  return (
    <div
      className={cn(
        "rounded-nb-md px-5 py-4 pb-5",
        "bg-status-danger/[0.10] border border-status-danger/40",
      )}
    >
      <div className="flex items-center justify-between mb-3">
        <h3 className="m-0 text-[13px] font-bold tracking-[0.14em] uppercase text-[#E4A096] flex items-center gap-2">
          <Pulser />
          Failing · sorted by impact
        </h3>
        <span className="font-mono text-[11px] text-[#E4A096] tracking-wider">
          click ACK to silence
          {freshUnacked > 0
            ? ` · ${freshUnacked} unacked in the last min`
            : ""}
        </span>
      </div>
      <div className="grid grid-cols-4 gap-2.5">
        {endpoints.map((e) => (
          <BigCard
            key={e.id}
            endpoint={e}
            tone="fail"
            now={now}
            onAck={onAck}
            onUnack={onUnack}
          />
        ))}
      </div>
    </div>
  );
};

const WatchingBand = ({ endpoints, onAck, onUnack, now }: BandProps) => {
  if (endpoints.length === 0) return null;
  return (
    <div className="grid grid-cols-4 gap-2.5">
      {endpoints.map((e) => (
        <BigCard
          key={e.id}
          endpoint={e}
          tone="warn"
          now={now}
          onAck={onAck}
          onUnack={onUnack}
        />
      ))}
    </div>
  );
};

const HealthyStrip = ({ endpoints }: { endpoints: MonitorEndpoint[] }) => {
  if (endpoints.length === 0) return null;
  return (
    <div className="rounded-nb-md border border-[#2A2620] bg-[#15130E] px-4 py-3.5">
      <div className="flex items-center justify-between mb-2.5">
        <h3 className="m-0 text-[13px] font-bold tracking-[0.14em] uppercase text-[#7FD2A6] flex items-center gap-2">
          <span aria-hidden="true">●</span>
          Healthy · {endpoints.length} endpoint{endpoints.length === 1 ? "" : "s"}
        </h3>
        <span className="font-mono text-[11px] text-[#7FD2A6] tracking-wider">
          all clear
        </span>
      </div>
      <div className="grid grid-cols-6 gap-2">
        {endpoints.map((e) => (
          <HealthyChip key={e.id} endpoint={e} />
        ))}
      </div>
    </div>
  );
};

const HealthyChip = ({ endpoint }: { endpoint: MonitorEndpoint }) => {
  // Throughput proxy — the only counter that exists on the snapshot for
  // "this thing is doing work" without us having to derive rate. Use it
  // sparingly: the chip's primary job is to show the dot pulsing.
  const pending = endpoint.status.pendingCount ?? 0;
  const deferred = endpoint.status.deferredCount ?? 0;
  return (
    <div
      className={cn(
        "rounded-md bg-[#1A1814] border border-[#2A2620] border-l-[3px] border-l-status-success",
        "px-2.5 py-2 flex items-center gap-2 text-xs font-semibold text-[#C9C1AB]",
      )}
    >
      <span className="w-[7px] h-[7px] rounded-full bg-status-success shrink-0 nb-monitor-pulseg" />
      <span className="flex-1 min-w-0 truncate">{endpoint.id}</span>
      {(pending > 0 || deferred > 0) && (
        <span className="font-mono text-[10.5px] text-[#6F6A5C] font-medium">
          {formatBigNumber(pending + deferred)}
        </span>
      )}
    </div>
  );
};

/* =====================================================================
   Big card — used for both Failing and Watching bands
   ===================================================================== */

interface BigCardProps {
  endpoint: MonitorEndpoint;
  tone: "fail" | "warn";
  now: number;
  onAck: (id: string, reason?: string) => void;
  onUnack: (id: string) => void;
}

const BigCard = ({ endpoint, tone, now, onAck, onUnack }: BigCardProps) => {
  const failed = endpoint.status.failedCount ?? 0;
  const pending = endpoint.status.pendingCount ?? 0;
  const deferred = endpoint.status.deferredCount ?? 0;
  const acked = Boolean(endpoint.ack);
  const isWatching = tone === "warn";
  const heroIsPending = isWatching && pending > failed;
  const heroValue = heroIsPending ? pending : failed;
  const heroLabel = heroIsPending ? "pending" : "failed";

  // The pulse is what makes the wall ask for attention. We only flash a
  // card during its first 60 s of newness *and* only if no operator has
  // already silenced it.
  const pulse = endpoint.isFreshFailure && !acked;

  const sinceText = (() => {
    if (acked) {
      const ago = relativeAgo(now - endpoint.ack!.ackedAt);
      const reason = endpoint.ack!.reason
        ? ` · "${truncate(endpoint.ack!.reason, 24)}"`
        : "";
      return `Acked${reason} · ${ago}`;
    }
    if (endpoint.firstFailureAt) {
      return `Failing since ${relativeAgo(now - endpoint.firstFailureAt)} ago`;
    }
    if (endpoint.status.eventTime) {
      return `Last update ${endpoint.status.eventTime.fromNow()}`;
    }
    return "—";
  })();

  // Trend reads from the sample-history failed counts so it stays meaningful
  // even when the API doesn't track failure-arrival rate directly.
  const trendValues = endpoint.samples.map((s) =>
    heroIsPending ? s.pending : s.failed,
  );

  const toneCls =
    tone === "fail"
      ? acked
        ? "bg-[#1A1814] border-[#2A2620] opacity-80"
        : "bg-gradient-to-b from-status-danger/[0.18] to-[#1A1814] border-status-danger/55"
      : "bg-gradient-to-b from-status-warning/[0.14] to-[#1A1814] border-status-warning/45";

  const sIconCls =
    tone === "fail"
      ? "bg-status-danger text-white"
      : "bg-status-warning text-ink";

  const heroCls =
    tone === "fail"
      ? acked
        ? "text-[#C9C1AB]"
        : "text-[#F4D9D3]"
      : "text-[#F6E7C7]";

  const labelCls =
    tone === "fail"
      ? acked
        ? "text-[#8A8473]"
        : "text-[#E4A096]"
      : "text-[#E7C783]";

  const trendStroke = describeTrend(trendValues).stroke;
  const rateText = describeRate(endpoint.ratePerMin);
  const drainEta = isWatching ? describeDrainEta(endpoint) : undefined;

  return (
    <div
      className={cn(
        "rounded-nb-md border px-4 py-3.5 flex flex-col gap-2 relative overflow-hidden",
        toneCls,
        pulse && "nb-monitor-pulse-card",
      )}
    >
      <div className="flex items-center gap-2">
        <span
          className={cn(
            "w-[22px] h-[22px] rounded-full inline-flex items-center justify-center shrink-0",
            "font-mono font-extrabold text-[11px]",
            sIconCls,
          )}
        >
          {tone === "fail" ? "!" : "⏱"}
        </span>
        <span className="font-bold text-[15px] tracking-tight flex-1 min-w-0 truncate text-[#F4F2EA]">
          {endpoint.id}
        </span>
        <AckButton acked={acked} onAck={() => onAck(endpoint.id)} onUnack={() => onUnack(endpoint.id)} />
      </div>

      <div className="flex items-baseline gap-2">
        <span
          className={cn(
            "font-extrabold tracking-tight tabular-nums leading-[0.95] text-[48px]",
            heroCls,
          )}
        >
          {formatBigNumber(heroValue)}
        </span>
        <span
          className={cn(
            "font-mono text-[11px] tracking-[0.14em] uppercase font-bold",
            labelCls,
          )}
        >
          {heroLabel}
        </span>
      </div>

      <div className="grid grid-cols-3 gap-1.5 font-mono text-[10.5px] text-[#8A8473]">
        <Stat
          label={heroIsPending ? "Failed" : "Pending"}
          value={heroIsPending ? failed : pending}
          tone={heroIsPending ? (failed > 0 ? "bad" : undefined) : pending > 0 ? "warn" : undefined}
        />
        <Stat
          label="Deferred"
          value={deferred}
          tone={deferred > 0 ? "warn" : undefined}
        />
        <Stat
          label={isWatching ? "Drain ETA" : "Rate / min"}
          value={isWatching ? drainEta ?? "—" : rateText}
          tone={!isWatching && (endpoint.ratePerMin ?? 0) > 0 ? "bad" : undefined}
        />
      </div>

      <div className="flex justify-between items-center font-mono text-[10px] text-[#6F6A5C] pt-1.5 border-t border-[#2A2620]">
        <span
          className={cn(
            acked
              ? "text-[#8A8473]"
              : tone === "fail"
                ? "text-[#E4A096] font-bold"
                : "text-[#E7C783]",
          )}
        >
          {sinceText}
        </span>
        <Sparkline values={trendValues} stroke={trendStroke} />
      </div>
    </div>
  );
};

const Stat = ({
  label,
  value,
  tone,
}: {
  label: string;
  value: number | string;
  tone?: "bad" | "warn";
}) => (
  <div className="flex flex-col gap-0.5">
    <span>{label}</span>
    <span
      className={cn(
        "text-sm font-bold tabular-nums",
        tone === "bad"
          ? "text-[#E4A096]"
          : tone === "warn"
            ? "text-[#E7C783]"
            : "text-[#C9C1AB]",
      )}
    >
      {typeof value === "number" ? formatBigNumber(value) : value}
    </span>
  </div>
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
      "font-mono text-[9px] tracking-wider uppercase font-semibold",
      "px-1.5 py-0.5 rounded-sm border",
      acked
        ? "bg-status-success/[0.16] text-[#7FD2A6] border-status-success/40"
        : "bg-transparent text-[#8A8473] border-[#3a352d] hover:text-[#F4F2EA] hover:border-[#5a554b]",
      "transition-colors cursor-pointer",
    )}
    title={acked ? "Click to clear acknowledgement" : "Acknowledge this failure (auto-expires after 4 h or on recovery)"}
  >
    {acked ? "✓ acked" : "ack"}
  </button>
);

/* =====================================================================
   Sparkline + visual primitives
   ===================================================================== */

interface SparklineProps {
  values: number[];
  stroke: string;
  width?: number;
  height?: number;
}

const Sparkline = ({ values, stroke, width = 36, height = 10 }: SparklineProps) => {
  if (values.length < 2) {
    return (
      <svg width={width} height={height} viewBox={`0 0 ${width} ${height}`}>
        <line
          x1={0}
          y1={height / 2}
          x2={width}
          y2={height / 2}
          stroke="#C9C1AB"
          strokeWidth={1.5}
        />
      </svg>
    );
  }
  const min = Math.min(...values);
  const max = Math.max(...values);
  const range = max - min || 1;
  const step = width / (values.length - 1);
  const points = values
    .map((v, i) => {
      // Higher = "worse" → draw at the top of the box; lower = "better" → bottom.
      const y = height - ((v - min) / range) * (height - 2) - 1;
      return `${(i * step).toFixed(1)},${y.toFixed(1)}`;
    })
    .join(" ");
  return (
    <svg width={width} height={height} viewBox={`0 0 ${width} ${height}`}>
      <polyline points={points} fill="none" stroke={stroke} strokeWidth={1.5} />
    </svg>
  );
};

const Pulser = () => (
  <span
    aria-hidden="true"
    className="inline-block w-[9px] h-[9px] rounded-full bg-status-danger nb-monitor-pulser align-middle"
  />
);

const LogoMark = ({ size = 22 }: { size?: number }) => (
  <svg
    width={size}
    height={size}
    viewBox="0 0 36 36"
    fill="none"
    className="inline-block translate-y-[2px]"
  >
    <path
      d="M5 22c0-3.5 2.8-6.3 6.3-6.3.7 0 1.4.1 2 .3.8-3.7 4.1-6.5 8-6.5 4.2 0 7.6 3.2 8.1 7.2 2.7.4 4.8 2.7 4.8 5.5 0 3.1-2.5 5.5-5.5 5.5H11.3C7.8 27.7 5 25 5 22z"
      stroke="#F4F2EA"
      strokeWidth={2.5}
      strokeLinejoin="round"
    />
    <circle cx={29} cy={11} r={3} fill="#E8743C" />
  </svg>
);

/* =====================================================================
   Footer (ticker + legend) and stale banner
   ===================================================================== */

const WallFoot = ({ ticker }: { ticker: TickerEvent[] }) => (
  <div className="flex items-center gap-6 mt-1 font-mono text-[11px] text-[#8A8473] tracking-wider">
    <div className="flex gap-3.5 items-center">
      <LegendKey color="bg-status-danger">Failed</LegendKey>
      <LegendKey color="bg-status-warning">Pending / backlog</LegendKey>
      <LegendKey color="bg-status-success">Healthy</LegendKey>
      <LegendKey color="bg-[#3a352d]">Idle</LegendKey>
    </div>
    <Ticker events={ticker} />
  </div>
);

const LegendKey = ({
  color,
  children,
}: {
  color: string;
  children: React.ReactNode;
}) => (
  <span className="inline-flex items-center gap-1.5">
    <span aria-hidden="true" className={cn("w-2.5 h-2.5 rounded-[3px]", color)} />
    {children}
  </span>
);

const Ticker = ({ events }: { events: TickerEvent[] }) => {
  // The ticker is the only thing on the wall that *moves* in the absence of
  // incidents. That's intentional — it also doubles as a liveness signal,
  // proving the page hasn't frozen. Pause animation on hover for legibility.
  if (events.length === 0) {
    return (
      <div className="ml-auto flex-1 overflow-hidden whitespace-nowrap pl-4 border-l border-[#2A2620] text-[#6F6A5C]">
        Waiting for activity…
      </div>
    );
  }
  return (
    <div className="ml-auto flex-1 overflow-hidden pl-4 border-l border-[#2A2620]">
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
      ? "text-[#E4A096]"
      : event.kind === "recovery"
        ? "text-[#7FD2A6]"
        : "text-[#C9C1AB]";
  const verb =
    event.kind === "failure"
      ? "failed"
      : event.kind === "recovery"
        ? "recovered"
        : event.kind === "ack"
          ? "acked"
          : "ack cleared";
  return (
    <span className="inline-block mr-8">
      <span className="text-[#6F6A5C] mr-1.5">{ts}</span>
      <span className={cn("font-semibold", labelCls)}>{event.endpoint}</span>
      <span className="ml-1">
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
        "rounded-nb-md px-4 py-3 flex items-center gap-3",
        "bg-status-danger/20 border border-status-danger/60 text-[#F4D9D3]",
        "font-mono text-[12px] tracking-wider",
      )}
      role="alert"
    >
      <Pulser />
      <span className="font-bold uppercase">Connection lost</span>
      <span className="text-[#E4A096]">
        Last update {lastTs} · {seconds}s ago · retrying every 5 s
      </span>
    </div>
  );
};

/* =====================================================================
   Pure helpers — splitting bands, formatting, sparkline math
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
    const failed = e.status.failedCount ?? 0;
    const pending = e.status.pendingCount ?? 0;
    const deferred = e.status.deferredCount ?? 0;
    if (failed > 0) {
      failing.push(e);
    } else if (pending > 0 || deferred > 0) {
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
  watching.sort(
    (a, b) =>
      (b.status.pendingCount ?? 0) + (b.status.deferredCount ?? 0) -
      ((a.status.pendingCount ?? 0) + (a.status.deferredCount ?? 0)),
  );
  // Healthy: alphabetical so chip placement is stable across refreshes —
  // a chip jumping around because traffic ticked up is a visual distraction
  // on the wall.
  healthy.sort((a, b) => a.id.localeCompare(b.id));
  return { failing, watching, healthy };
}

function summarize(endpoints: MonitorEndpoint[]): SummaryShape {
  let failingCount = 0;
  let ackedFailingCount = 0;
  let freshFailures = 0;
  let unackedFresh = 0;
  let pendingTotal = 0;
  let pendingDeltaPerMin = 0;
  let failedTotal = 0;
  let failedRatePerHour = 0;
  let healthyCount = 0;

  for (const e of endpoints) {
    const failed = e.status.failedCount ?? 0;
    const pending = e.status.pendingCount ?? 0;
    const deferred = e.status.deferredCount ?? 0;
    failedTotal += failed;
    pendingTotal += pending;
    if (failed > 0) {
      failingCount += 1;
      if (e.ack) ackedFailingCount += 1;
      if (e.isFreshFailure) {
        freshFailures += 1;
        if (!e.ack) unackedFresh += 1;
      }
      if (e.ratePerMin && e.ratePerMin > 0) {
        failedRatePerHour += e.ratePerMin * 60;
      }
    } else if (pending === 0 && deferred === 0) {
      healthyCount += 1;
    }
    // Pending rate uses the same sample window we keep for the sparkline.
    if (e.samples.length >= 2) {
      const oldest = e.samples[0];
      const latest = e.samples[e.samples.length - 1];
      const dt = latest.t - oldest.t;
      if (dt > 0) {
        pendingDeltaPerMin += ((latest.pending - oldest.pending) / dt) * 60_000;
      }
    }
  }

  return {
    totalEndpoints: endpoints.length,
    failingCount,
    ackedFailingCount,
    freshFailuresLast5m: freshFailures,
    unackedFreshFailures: unackedFresh,
    pendingTotal,
    pendingDeltaPerMin,
    failedTotal,
    failedRatePerHour,
    healthyCount,
  };
}

function describeTrend(values: number[]): { stroke: string } {
  if (values.length < 2) return { stroke: "#C9C1AB" };
  const first = values[0];
  const last = values[values.length - 1];
  if (last > first) return { stroke: "#E4A096" };
  if (last < first) return { stroke: "#7FD2A6" };
  return { stroke: "#C9C1AB" };
}

function describeRate(rate: number | undefined): string {
  if (rate === undefined) return "—";
  const rounded = Math.round(rate);
  if (rounded === 0) return "stable";
  return rounded > 0 ? `▲ ${rounded}` : `▼ ${Math.abs(rounded)}`;
}

function describeDrainEta(endpoint: MonitorEndpoint): string | undefined {
  // ETA = pending / drain-rate-per-min. We estimate the drain rate from the
  // sample history: if pending is decreasing, the slope is our drain rate.
  // If it's flat or rising, we can't promise an ETA — surface "—" instead of
  // lying. Better silent than wrong on a wall display.
  if (endpoint.samples.length < 2) return undefined;
  const oldest = endpoint.samples[0];
  const latest = endpoint.samples[endpoint.samples.length - 1];
  const dt = latest.t - oldest.t;
  if (dt <= 0) return undefined;
  const drainPerMin = ((oldest.pending - latest.pending) / dt) * 60_000;
  if (drainPerMin <= 0) return latest.pending > 0 ? "growing" : "—";
  const minutes = latest.pending / drainPerMin;
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

function relativeAgo(ms: number): string {
  const s = Math.max(0, Math.round(ms / 1000));
  if (s < 60) return `${s}s`;
  const m = Math.round(s / 60);
  if (m < 60) return `${m} min`;
  const h = Math.round(m / 60);
  if (h < 24) return `${h} h`;
  return `${Math.round(h / 24)} d`;
}

function truncate(s: string, n: number): string {
  return s.length > n ? `${s.slice(0, n - 1)}…` : s;
}
