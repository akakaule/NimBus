import { useCallback, useEffect, useMemo, useState } from "react";
import { Link } from "react-router-dom";
import {
  CartesianGrid,
  Line,
  LineChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import * as api from "api-client";
import ByEventTypeTab from "components/metrics/by-event-type-tab";
import Page from "components/page";
import { Spinner } from "components/ui/spinner";
import { EmptyState } from "components/ui/empty-state";
import { StatRow, StatTile, type StatTileTone } from "components/ui/stat-tile";
import { Tab, TabList, TabPanel, TabPanels, Tabs } from "components/ui/tabs";
import { useUrlFilters } from "hooks/use-url-filters";
import { cn } from "lib/utils";

// Tab + event-type filter state lives in the URL (`?tab=by-event-type&types=X`)
// so views are linkable and browser Back restores them. Stable module-level
// defaults per the use-url-filters contract.
const TAB_NAMES = ["overview", "by-event-type"] as const;
const FILTER_DEFAULTS = { tab: "overview", types: [] as string[] };

const PERIODS: { label: string; value: api.Period }[] = [
  { label: "1h", value: api.Period._1h },
  { label: "12h", value: api.Period._12h },
  { label: "1d", value: api.Period._1d },
  { label: "3d", value: api.Period._3d },
  { label: "7d", value: api.Period._7d },
  { label: "30d", value: api.Period._30d },
];

// Latency thresholds — what counts as "slow" (warn) vs an outlier (danger).
// Pulled out so KPI tones, the health strip, and the latency-row badges all
// agree on the same cut-offs.
const SLOW_MS = 500;
const OUTLIER_MS = 1000;

// Design palette aligned with the Tailwind theme. Inlined as hex because
// SVG fills don't resolve Tailwind CSS variables at parse time.
const PALETTE = {
  published: "#3A6FB0",
  handled: "#2E8F5E",
  failed: "#C2412E",
  warning: "#C98A1B",
  purple: "#6B3FA3",
  grid: "#E5DFCE",
  ink3: "#8A8473",
} as const;

function formatMs(ms: number | undefined): string {
  if (ms == null) return "—";
  if (ms >= 1000) {
    const s = ms / 1000;
    return s >= 10 ? `${s.toFixed(0)} s` : `${s.toFixed(1)} s`;
  }
  return `${Math.round(ms)} ms`;
}

function formatNumber(n: number): string {
  return n.toLocaleString();
}

function formatTimestamp(
  ts: string | undefined,
  bucketSize: string | undefined,
): string {
  if (!ts) return "";
  let iso = ts;
  if (ts.length === 10) iso = ts + "T00:00:00Z";
  else if (ts.length === 13) iso = ts + ":00:00Z";
  else if (ts.length === 16) iso = ts + ":00Z";
  else if (!ts.endsWith("Z")) iso = ts + "Z";

  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return ts;

  // Render axis labels in the browser's local timezone — the rest of the
  // WebApp (formatMoment, etc.) already does the same via moment().format().
  const day = String(d.getDate()).padStart(2, "0");
  const mon = String(d.getMonth() + 1).padStart(2, "0");
  const hr = String(d.getHours()).padStart(2, "0");
  const min = String(d.getMinutes()).padStart(2, "0");

  if (bucketSize === "day") return `${day}/${mon}`;
  if (bucketSize === "minute") return `${hr}:${min}`;
  return `${hr}:00`;
}

export default function Metrics() {
  const [overview, setOverview] = useState<api.MetricsOverview | null>(null);
  const [latency, setLatency] = useState<api.LatencyOverview | null>(null);
  const [timeseries, setTimeseries] = useState<api.TimeSeriesOverview | null>(
    null,
  );
  const [insights, setInsights] = useState<api.FailedInsightsOverview | null>(
    null,
  );
  const [byEventType, setByEventType] =
    useState<api.EventTypeTimeSeriesOverview | null>(null);
  const [loading, setLoading] = useState(true);
  const [period, setPeriod] = useState<api.Period>(api.Period._1d);

  const { applied, applyFilters } = useUrlFilters(FILTER_DEFAULTS);
  const tabIndex = Math.max(
    0,
    (TAB_NAMES as readonly string[]).indexOf(applied.tab),
  );

  const fetchAll = useCallback(async (p: api.Period) => {
    setLoading(true);
    try {
      const client = new api.Client(api.CookieAuth());
      // Insights is optional — Metrics still renders if the call fails so a
      // misconfigured /metrics/failed-insights route doesn't blank the page.
      const [o, l, t, i, e] = await Promise.all([
        client.getMetricsOverview(p),
        client.getMetricsLatency(p).catch(() => null),
        client.getMetricsTimeseries(p).catch(() => null),
        client.getMetricsFailedInsights(p).catch(() => null),
        client.getMetricsTimeseriesByEventtype(p).catch(() => null),
      ]);
      setOverview(o);
      setLatency(l);
      setTimeseries(t);
      setInsights(i);
      setByEventType(e);
    } catch (err) {
      console.error("Failed to load metrics", err);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void fetchAll(period);
  }, [period, fetchAll]);

  const periodLabel = PERIODS.find((p) => p.value === period)?.label ?? "";

  // ---- Derived totals ----------------------------------------------------
  const totals = useMemo(() => {
    const handled = (overview?.handled ?? []).reduce(
      (s, x) => s + (x.count ?? 0),
      0,
    );
    const failed = (overview?.failed ?? []).reduce(
      (s, x) => s + (x.count ?? 0),
      0,
    );
    const published = (overview?.published ?? []).reduce(
      (s, x) => s + (x.count ?? 0),
      0,
    );
    const total = handled + failed;
    const failureRate = total > 0 ? (failed / total) * 100 : 0;
    return { handled, failed, published, total, failureRate };
  }, [overview]);

  // ---- Latency rollup ----------------------------------------------------
  // Single source of truth for everything downstream — KPI cards, the health
  // strip, and the latency table all pull from these per-route summaries.
  const latRows: LatRow[] = useMemo(() => {
    return (latency?.latencies ?? []).map((l) => {
      const queueAvg = l.queue?.avgMs ?? 0;
      const procAvg = l.processing?.avgMs ?? 0;
      const procMax = l.processing?.maxMs ?? 0;
      const totalAvg = queueAvg + procAvg;
      const queueRatio = totalAvg > 0 ? queueAvg / totalAvg : 0;
      return {
        endpointId: l.endpointId ?? "",
        eventTypeId: l.eventTypeId ?? "",
        count: l.processing?.count ?? l.queue?.count ?? 0,
        queueAvg,
        procAvg,
        procMax,
        totalAvg,
        queueRatio,
      };
    });
  }, [latency]);

  const latencyKpis = useMemo(() => {
    if (latRows.length === 0) {
      return { avgProc: 0, maxProc: 0, slow: 0, outliers: 0, totalRoutes: 0 };
    }
    let weighted = 0;
    let weight = 0;
    let maxProc = 0;
    let slow = 0;
    let outliers = 0;
    for (const r of latRows) {
      const c = Math.max(r.count, 1);
      weighted += r.procAvg * c;
      weight += c;
      if (r.procMax > maxProc) maxProc = r.procMax;
      if (r.procAvg > SLOW_MS) slow += 1;
      if (r.procMax > OUTLIER_MS) outliers += 1;
    }
    return {
      avgProc: weight > 0 ? weighted / weight : 0,
      maxProc,
      slow,
      outliers,
      totalRoutes: latRows.length,
    };
  }, [latRows]);

  // ---- Per-endpoint roll-ups for the published / consumed cards ----------
  const publishedByEndpoint = useMemo(
    () => rollupByEndpoint(overview?.published ?? []),
    [overview?.published],
  );
  const consumedByEndpoint = useMemo(
    () => rollupByEndpoint(overview?.handled ?? []),
    [overview?.handled],
  );

  // ---- Failure summary --------------------------------------------------
  const topFailures = useMemo(
    () => (insights?.groups ?? []).slice(0, 3),
    [insights],
  );
  const totalFailureCount = insights?.totalFailed ?? totals.failed;

  // ---- Tone heuristics --------------------------------------------------
  const failureRateTone: StatTileTone =
    totals.failureRate >= 1
      ? "danger"
      : totals.failureRate > 0
        ? "warning"
        : "default";
  const maxLatencyTone: StatTileTone =
    latencyKpis.maxProc >= OUTLIER_MS
      ? "danger"
      : latencyKpis.maxProc >= SLOW_MS
        ? "warning"
        : "default";
  const avgLatencyTone: StatTileTone =
    latencyKpis.avgProc >= SLOW_MS ? "warning" : "default";
  const outliersTone: StatTileTone =
    latencyKpis.outliers > 0 ? "danger" : "default";

  return (
    <Page
      title="Metrics"
      subtitle="Bus-wide throughput, latency, and failure rates"
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
        </>
      }
    >
      <Tabs
        index={tabIndex}
        onTabChange={(i) =>
          applyFilters({ ...applied, tab: TAB_NAMES[i] ?? "overview" })
        }
        className="w-full"
      >
        <TabList className="mb-4">
          <Tab index={0}>Overview</Tab>
          <Tab index={1}>By event type</Tab>
        </TabList>
        <TabPanels>
          <TabPanel index={0} isLazy={false}>
            <div className="flex flex-col w-full gap-4 pb-8">
              {/* KPI strip — 5 hero numbers. Latency is hoisted out of the bottom
            table and into the headline so the question "is anything slow?"
            answers itself on page load (design §03 · pin 1). */}
              <StatRow columns={5}>
                <StatTile
                  label="Total Messages"
                  value={loading ? "—" : formatNumber(totals.total)}
                  delta={
                    loading
                      ? undefined
                      : totals.total === 0
                        ? "no traffic yet"
                        : `${formatNumber(totals.handled)} handled · ${formatNumber(totals.failed)} failed`
                  }
                  tone="muted"
                />
                <StatTile
                  label="Failure Rate"
                  value={loading ? "—" : `${totals.failureRate.toFixed(2)}%`}
                  delta={
                    loading
                      ? undefined
                      : totals.failed === 0
                        ? `all clear · ${periodLabel}`
                        : `${formatNumber(totals.failed)} of ${formatNumber(totals.total)} · ${periodLabel}`
                  }
                  tone={failureRateTone}
                />
                <StatTile
                  label="Avg Latency"
                  value={loading ? "—" : formatMs(latencyKpis.avgProc)}
                  delta={
                    loading
                      ? undefined
                      : latencyKpis.totalRoutes === 0
                        ? "no samples"
                        : `processing · ${latencyKpis.totalRoutes} route${latencyKpis.totalRoutes === 1 ? "" : "s"}`
                  }
                  tone={avgLatencyTone}
                />
                <StatTile
                  label="Max Latency"
                  value={loading ? "—" : formatMs(latencyKpis.maxProc)}
                  delta={
                    loading
                      ? undefined
                      : latencyKpis.maxProc === 0
                        ? "no samples"
                        : latencyKpis.outliers > 0
                          ? `${latencyKpis.outliers} outlier route${latencyKpis.outliers === 1 ? "" : "s"}`
                          : "within budget"
                  }
                  tone={maxLatencyTone}
                />
                <StatTile
                  label="Outliers"
                  value={
                    loading
                      ? "—"
                      : latencyKpis.outliers === 0
                        ? "0"
                        : formatNumber(latencyKpis.outliers)
                  }
                  delta={
                    loading
                      ? undefined
                      : latencyKpis.outliers === 0
                        ? `under ${formatMs(OUTLIER_MS)} · ${periodLabel}`
                        : `route${latencyKpis.outliers === 1 ? "" : "s"} over ${formatMs(OUTLIER_MS)}`
                  }
                  tone={outliersTone}
                />
              </StatRow>

              {loading ? (
                <div className="flex justify-center items-center py-20">
                  <Spinner size="lg" />
                </div>
              ) : (
                <>
                  {/* Activity chart — Published + Handled on shared axis, failed
                events as red dots at the baseline (design §03 · pin 2). */}
                  <ChartCard
                    title="Activity & failures"
                    meta={`shared time axis · ${periodLabel}`}
                    legend={
                      <>
                        <LegendArea color={PALETTE.published}>
                          Published
                        </LegendArea>
                        <LegendArea color={PALETTE.handled}>Handled</LegendArea>
                        <LegendDot color={PALETTE.failed}>
                          Failed events
                        </LegendDot>
                      </>
                    }
                  >
                    <ActivityChart
                      dataPoints={timeseries?.dataPoints ?? []}
                      bucketSize={timeseries?.bucketSize}
                    />
                  </ChartCard>

                  {/* Top failure causes — compact teaser linking to /Insights. We
                deliberately don't duplicate the full grouped table here;
                Metrics is "what's happening", Insights is "why" (design §04). */}
                  {topFailures.length > 0 && (
                    <FailureSummary
                      groups={topFailures}
                      totalFailed={totalFailureCount ?? 0}
                    />
                  )}

                  {/* Published × Consumed — symmetric small-multiples instead of a
                stacked horizontal bar that mixes "who publishes most" with
                "what's the type split" (design §03 · pin 4). */}
                  <div className="grid grid-cols-1 lg:grid-cols-2 gap-3">
                    <ChartCard
                      title="Published by endpoint"
                      meta={`top 5 · ${periodLabel}`}
                    >
                      <BarList
                        rows={publishedByEndpoint.slice(0, 5)}
                        color={PALETTE.published}
                        emptyLabel="No published messages in this window."
                      />
                    </ChartCard>
                    <ChartCard
                      title="Consumed by endpoint"
                      meta={`top 5 · ${periodLabel}`}
                    >
                      <BarList
                        rows={consumedByEndpoint.slice(0, 5)}
                        color={PALETTE.handled}
                        emptyLabel="No handled messages in this window."
                      />
                    </ChartCard>
                  </div>

                  {/* Latency by route — single row per route with a queue/processing
                ratio bar so the operator can tell "scale subscribers" from
                "profile the handler" at a glance (design §03 · pin 5). */}
                  <ChartCard
                    title="Latency by route"
                    meta="queue + processing · sorted by max desc"
                  >
                    {latRows.length === 0 ? (
                      <EmptyState
                        icon="—"
                        title="No latency samples in this window"
                        description="Latency is recorded when handlers complete. Try a wider time range."
                      />
                    ) : (
                      <LatencyTable rows={latRows} />
                    )}
                  </ChartCard>
                </>
              )}
            </div>
          </TabPanel>
          <TabPanel index={1} isLazy={false}>
            <ByEventTypeTab
              data={byEventType}
              overview={overview}
              periodLabel={periodLabel}
              loading={loading}
              selectedTypes={applied.types}
              onSelectedTypesChange={(types) =>
                applyFilters({ ...applied, types })
              }
            />
          </TabPanel>
        </TabPanels>
      </Tabs>
    </Page>
  );
}

// =========================================================================
// Components
// =========================================================================

interface ChartCardProps {
  title: string;
  meta?: string;
  legend?: React.ReactNode;
  children: React.ReactNode;
}

const ChartCard: React.FC<ChartCardProps> = ({
  title,
  meta,
  legend,
  children,
}) => (
  <div className="bg-card border border-border rounded-nb-md p-4">
    <div className="flex items-baseline justify-between gap-4 mb-2 flex-wrap">
      <h4 className="m-0 text-sm font-bold tracking-tight">{title}</h4>
      <div className="flex items-center gap-4 flex-wrap">
        {legend && (
          <div className="flex items-center gap-3 font-mono text-[11px] text-muted-foreground">
            {legend}
          </div>
        )}
        {meta && (
          <span className="font-mono text-[11px] text-muted-foreground">
            {meta}
          </span>
        )}
      </div>
    </div>
    {children}
  </div>
);

const LegendArea: React.FC<{ color: string; children: React.ReactNode }> = ({
  color,
  children,
}) => (
  <span className="inline-flex items-center gap-1.5">
    <span
      aria-hidden="true"
      className="inline-block w-2.5 h-[3px]"
      style={{ background: color }}
    />
    {children}
  </span>
);

const LegendDot: React.FC<{ color: string; children: React.ReactNode }> = ({
  color,
  children,
}) => (
  <span className="inline-flex items-center gap-1.5">
    <span
      aria-hidden="true"
      className="inline-block w-2 h-2 rounded-full"
      style={{ background: color }}
    />
    {children}
  </span>
);

// -------------------------------------------------------------------------
// Activity chart — uses the same Recharts line treatment as the event-type
// chart. Failures remain baseline markers so they flag affected buckets
// without changing the scale used to compare published and handled traffic.
// -------------------------------------------------------------------------
interface ActivityChartProps {
  dataPoints: api.TimeSeriesDataPoint[];
  bucketSize?: string;
}

interface ActivityChartRow {
  timestamp?: string;
  published: number;
  handled: number;
  failed: number;
  failureMarker: number | null;
}

export const ActivityChart: React.FC<ActivityChartProps> = ({
  dataPoints,
  bucketSize,
}) => {
  if (dataPoints.length === 0) {
    return (
      <EmptyState
        icon="—"
        title="No activity in this window"
        description="Either no events flowed, or telemetry hasn't caught up yet."
      />
    );
  }

  const rows: ActivityChartRow[] = dataPoints.map((point) => ({
    timestamp: point.timestamp,
    published: point.published ?? 0,
    handled: point.handled ?? 0,
    failed: point.failed ?? 0,
    failureMarker: (point.failed ?? 0) > 0 ? 0 : null,
  }));

  return (
    <ResponsiveContainer width="100%" height={280}>
      <LineChart data={rows} margin={{ top: 8, right: 16, bottom: 0, left: 0 }}>
        <CartesianGrid
          stroke={PALETTE.grid}
          strokeDasharray="3 3"
          vertical={false}
        />
        <XAxis
          dataKey="timestamp"
          tickFormatter={(timestamp: string) =>
            formatTimestamp(timestamp, bucketSize)
          }
          tick={{ fontSize: 10.5, fill: PALETTE.ink3 }}
          stroke={PALETTE.grid}
          tickLine={false}
          minTickGap={32}
        />
        <YAxis
          allowDecimals={false}
          tick={{ fontSize: 10.5, fill: PALETTE.ink3 }}
          stroke={PALETTE.grid}
          tickLine={false}
          width={44}
        />
        <Tooltip
          content={({ active, payload, label }) => {
            const point = payload?.[0]?.payload as ActivityChartRow | undefined;
            if (!active || !point) return null;
            return (
              <div className="bg-popover text-popover-foreground border border-border rounded-md shadow-lg px-3 py-2 text-[11.5px] min-w-[170px]">
                <div className="font-mono text-muted-foreground mb-1">
                  {formatTimestamp(String(label), bucketSize)}
                </div>
                <ActivityTooltipRow
                  color={PALETTE.published}
                  label="Published"
                  value={point.published}
                />
                <ActivityTooltipRow
                  color={PALETTE.handled}
                  label="Handled"
                  value={point.handled}
                />
                <ActivityTooltipRow
                  color={PALETTE.failed}
                  label="Failed"
                  value={point.failed}
                  emphasize={point.failed > 0}
                />
              </div>
            );
          }}
        />
        <Line
          type="monotone"
          dataKey="published"
          stroke={PALETTE.published}
          strokeWidth={2}
          dot={false}
          activeDot={{ r: 4 }}
          isAnimationActive={false}
        />
        <Line
          type="monotone"
          dataKey="handled"
          stroke={PALETTE.handled}
          strokeWidth={2}
          dot={false}
          activeDot={{ r: 4 }}
          isAnimationActive={false}
        />
        <Line
          dataKey="failureMarker"
          stroke="none"
          dot={<FailureDot />}
          activeDot={false}
          isAnimationActive={false}
        />
      </LineChart>
    </ResponsiveContainer>
  );
};

const FailureDot = ({
  cx,
  cy,
  payload,
}: {
  cx?: number;
  cy?: number;
  payload?: ActivityChartRow;
}) => {
  if (cx == null || cy == null || !payload || payload.failed <= 0) return null;
  const radius = Math.min(5, 2 + Math.log10(payload.failed + 1) * 2);
  return <circle cx={cx} cy={cy} r={radius} fill={PALETTE.failed} />;
};

const ActivityTooltipRow: React.FC<{
  color: string;
  label: string;
  value: number;
  emphasize?: boolean;
}> = ({ color, label, value, emphasize = false }) => (
  <div className="flex items-center gap-1.5">
    <span
      aria-hidden="true"
      className="inline-block w-2 h-2 rounded-sm"
      style={{ background: color }}
    />
    <span>{label}</span>
    <span
      className="ml-auto pl-3 font-mono font-bold tabular-nums"
      style={emphasize ? { color } : undefined}
    >
      {formatNumber(value)}
    </span>
  </div>
);

// -------------------------------------------------------------------------
// Top failure causes — compact teaser linking to /Insights. Metrics shows
// "what" failed at a glance; clicking through to Insights tells the operator
// "why" (design §04 division of labour).
// -------------------------------------------------------------------------
interface FailureSummaryProps {
  groups: api.ErrorPatternGroup[];
  totalFailed: number;
}

const FailureSummary: React.FC<FailureSummaryProps> = ({
  groups,
  totalFailed,
}) => (
  <div
    className={cn(
      "bg-card border rounded-nb-md p-4",
      "border-status-danger-50",
    )}
  >
    <div className="flex items-baseline justify-between gap-4 mb-3 flex-wrap">
      <h4 className="m-0 text-sm font-bold tracking-tight text-status-danger">
        Top failure causes
      </h4>
      <div className="flex items-center gap-3 font-mono text-[11px] text-muted-foreground">
        <span>
          {totalFailed} failure{totalFailed === 1 ? "" : "s"} · {groups.length}{" "}
          pattern
          {groups.length === 1 ? "" : "s"}
        </span>
        <Link
          to="/Insights"
          className="text-primary-600 font-semibold no-underline hover:text-primary"
        >
          Open Insights ›
        </Link>
      </div>
    </div>
    <div className="overflow-x-auto">
      <table className="w-full text-[12.5px] tabular-nums">
        <thead>
          <tr className="bg-surface-2 text-muted-foreground">
            <th className="text-left font-semibold uppercase tracking-[0.06em] text-[10.5px] px-3 py-2">
              Error pattern
            </th>
            <th className="text-left font-semibold uppercase tracking-[0.06em] text-[10.5px] px-3 py-2">
              Endpoints / event types
            </th>
            <th className="text-right font-semibold uppercase tracking-[0.06em] text-[10.5px] px-3 py-2">
              Count
            </th>
            <th className="text-right font-semibold uppercase tracking-[0.06em] text-[10.5px] px-3 py-2">
              Latest
            </th>
            <th className="px-3 py-2" />
          </tr>
        </thead>
        <tbody>
          {groups.map((g, idx) => (
            <tr key={idx} className="border-t border-border">
              <td className="px-3 py-2.5 align-top">
                <div className="font-semibold text-foreground">
                  {g.errorCategory ?? "Unknown"}
                </div>
                {g.exampleErrorText && (
                  <div className="font-mono text-[10.5px] text-muted-foreground mt-0.5 truncate max-w-[420px]">
                    {g.exampleErrorText}
                  </div>
                )}
              </td>
              <td className="px-3 py-2.5 align-top">
                <div className="font-semibold">
                  {(g.endpoints ?? []).join(", ") || "—"}
                </div>
                <div className="font-mono text-[10.5px] text-nimbus-purple font-semibold">
                  {(g.eventTypes ?? []).join(", ") || "—"}
                </div>
              </td>
              <td className="px-3 py-2.5 text-right align-top text-status-danger font-bold">
                {g.count ?? 0}
              </td>
              <td className="px-3 py-2.5 text-right align-top font-mono text-[10.5px] text-muted-foreground">
                {g.latestOccurrence ? g.latestOccurrence.fromNow() : "—"}
              </td>
              <td className="px-3 py-2.5 text-right align-top">
                <Link
                  to="/Insights"
                  className="text-primary-600 font-semibold no-underline text-[12px] hover:text-primary"
                >
                  Investigate ›
                </Link>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
    <p className="mt-3 pt-2.5 border-t border-dashed border-border font-mono text-[11px] text-muted-foreground italic">
      Top 3 patterns shown as a teaser. Full grouping, stack traces, and replay
      actions live on{" "}
      <Link
        to="/Insights"
        className="text-primary-600 font-semibold no-underline not-italic hover:text-primary"
      >
        Insights
      </Link>
      .
    </p>
  </div>
);

// -------------------------------------------------------------------------
// Bar list — top-N endpoints with an inline horizontal bar. Symmetric
// Published / Consumed cards beat a single stacked-bar that mixes "who"
// with "what" (design §03 · pin 4).
// -------------------------------------------------------------------------
interface BarRow {
  endpointId: string;
  count: number;
}

const BarList: React.FC<{
  rows: BarRow[];
  color: string;
  emptyLabel: string;
}> = ({ rows, color, emptyLabel }) => {
  if (rows.length === 0) {
    return (
      <p className="text-[12.5px] text-muted-foreground italic m-0">
        {emptyLabel}
      </p>
    );
  }
  const max = Math.max(1, ...rows.map((r) => r.count));
  return (
    <div className="flex flex-col">
      {rows.map((r, i) => (
        <div
          key={r.endpointId}
          className={cn(
            "grid grid-cols-[150px_1fr_60px] gap-2.5 items-center py-1.5 text-[12.5px]",
            i > 0 && "border-t border-border",
          )}
        >
          <span className="font-semibold flex items-center gap-1.5 truncate">
            <span
              aria-hidden="true"
              className="w-2 h-2 rounded-sm shrink-0"
              style={{ background: color }}
            />
            <span className="truncate">{r.endpointId || "(unnamed)"}</span>
          </span>
          <svg
            viewBox="0 0 200 10"
            preserveAspectRatio="none"
            className="w-full h-2.5"
          >
            <rect
              x="0"
              y="2"
              width={(r.count / max) * 200}
              height="6"
              rx="2"
              fill={color}
            />
          </svg>
          <span className="font-mono font-bold text-right tabular-nums">
            {formatNumber(r.count)}
          </span>
        </div>
      ))}
    </div>
  );
};

// -------------------------------------------------------------------------
// Latency by route — single row per route with a queue:processing ratio
// bar. Operator sees "this is 80% queue → scale subscribers" vs "this is
// 80% processing → profile the handler" without doing arithmetic in their
// head (design §03 · pin 5).
// -------------------------------------------------------------------------
interface LatRow {
  endpointId: string;
  eventTypeId: string;
  count: number;
  queueAvg: number;
  procAvg: number;
  procMax: number;
  totalAvg: number;
  /** 0..1 — share of total avg latency spent in the queue (vs processing). */
  queueRatio: number;
}

const LatencyTable: React.FC<{ rows: LatRow[] }> = ({ rows }) => {
  // Sort by max desc by default — the worst route should sit on top so a
  // glance answers "is there an outlier?".
  const sorted = useMemo(
    () => [...rows].sort((a, b) => b.procMax - a.procMax),
    [rows],
  );
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-[12.5px] tabular-nums border-collapse">
        <thead>
          <tr className="bg-surface-2 text-muted-foreground">
            <th className="text-left font-semibold uppercase tracking-[0.06em] text-[10.5px] px-3 py-2 whitespace-nowrap">
              Route
            </th>
            <th className="text-left font-semibold uppercase tracking-[0.06em] text-[10.5px] px-3 py-2 whitespace-nowrap">
              Event Type
            </th>
            <th className="text-right font-semibold uppercase tracking-[0.06em] text-[10.5px] px-3 py-2 whitespace-nowrap">
              Count
            </th>
            <th className="text-left font-semibold uppercase tracking-[0.06em] text-[10.5px] px-3 py-2 whitespace-nowrap">
              Queue / processing
            </th>
            <th className="text-right font-semibold uppercase tracking-[0.06em] text-[10.5px] px-3 py-2 whitespace-nowrap">
              Avg
            </th>
            <th className="text-right font-semibold uppercase tracking-[0.06em] text-[10.5px] px-3 py-2 whitespace-nowrap">
              Max
            </th>
          </tr>
        </thead>
        <tbody>
          {sorted.map((r, i) => {
            const isOutlier = r.procMax >= OUTLIER_MS;
            const isSlow = !isOutlier && r.procAvg >= SLOW_MS;
            return (
              <tr
                key={`${r.endpointId}/${r.eventTypeId}/${i}`}
                className={cn(
                  "border-t border-border",
                  isOutlier && "bg-status-danger-50/60",
                  isSlow && "bg-status-warning-50/40",
                )}
              >
                <td className="px-3 py-2 font-semibold">{r.endpointId}</td>
                <td className="px-3 py-2 font-mono text-[11.5px] text-nimbus-purple font-semibold">
                  {r.eventTypeId}
                </td>
                <td className="px-3 py-2 text-right">{r.count}</td>
                <td className="px-3 py-2">
                  <div className="flex items-center gap-2.5">
                    <div className="flex h-2 w-[140px] bg-surface-2 rounded-full overflow-hidden">
                      <div
                        className="h-full"
                        style={{
                          width: `${r.queueRatio * 100}%`,
                          background: "#9DC0EA",
                        }}
                      />
                      <div
                        className="h-full"
                        style={{
                          width: `${(1 - r.queueRatio) * 100}%`,
                          background: PALETTE.warning,
                        }}
                      />
                    </div>
                    <span className="font-mono text-[10.5px] text-muted-foreground whitespace-nowrap">
                      {Math.round(r.queueRatio * 100)}% queue ·{" "}
                      {Math.round((1 - r.queueRatio) * 100)}% proc
                    </span>
                  </div>
                </td>
                <td
                  className={cn(
                    "px-3 py-2 text-right font-mono",
                    isSlow && "text-status-warning font-bold",
                  )}
                >
                  {formatMs(r.procAvg + r.queueAvg)}
                </td>
                <td
                  className={cn(
                    "px-3 py-2 text-right font-mono",
                    isOutlier && "text-status-danger font-bold",
                  )}
                >
                  {formatMs(r.procMax)}
                  {isOutlier && (
                    <span className="ml-1.5 inline-block bg-status-danger-50 text-status-danger-ink text-[10px] font-bold px-1.5 py-0.5 rounded-full align-middle">
                      outlier
                    </span>
                  )}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
};

// =========================================================================
// Helpers
// =========================================================================

function rollupByEndpoint(
  items: api.EndpointEventTypeMessageCount[],
): BarRow[] {
  const map = new Map<string, number>();
  for (const item of items) {
    const k = item.endpointId ?? "";
    map.set(k, (map.get(k) ?? 0) + (item.count ?? 0));
  }
  return Array.from(map.entries())
    .map(([endpointId, count]) => ({ endpointId, count }))
    .filter((r) => r.count > 0)
    .sort((a, b) => b.count - a.count);
}
