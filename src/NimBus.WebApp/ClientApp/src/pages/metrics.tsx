import { useState, useEffect, useCallback } from "react";
import * as api from "api-client";
import Page from "components/page";
import { Card, CardHeader, CardTitle, CardContent } from "components/ui/card";
import { Spinner } from "components/ui/spinner";
import { Badge } from "components/ui/badge";
import { StatRow, StatTile } from "components/ui/stat-tile";
import { EmptyState } from "components/ui/empty-state";
import { cn } from "lib/utils";
import {
  BarChart,
  Bar,
  AreaChart,
  Area,
  XAxis,
  YAxis,
  Tooltip,
  ResponsiveContainer,
  Legend,
  CartesianGrid,
} from "recharts";

// Design-system palette (replaces ad-hoc tailwind hex colours)
const CHART = {
  published: "#3A6FB0", // status-info
  handled: "#2E8F5E",   // status-success
  failed: "#C2412E",    // status-danger
  warning: "#C98A1B",   // status-warning
  purple: "#6B3FA3",    // nimbus-purple
  grid: "#E5DFCE",      // hairline / border
};

const PERIODS: { label: string; value: api.Period }[] = [
  { label: "1h", value: api.Period._1h },
  { label: "12h", value: api.Period._12h },
  { label: "1d", value: api.Period._1d },
  { label: "3d", value: api.Period._3d },
  { label: "7d", value: api.Period._7d },
  { label: "30d", value: api.Period._30d },
];

// Distinct fills for stacked bars (endpoint × event type). Drawn from the
// design palette plus a few neutral hues so adjacent stacks stay readable.
const COLORS = [
  CHART.published,
  CHART.handled,
  CHART.warning,
  CHART.failed,
  CHART.purple,
  "#3D5A80",
  "#7A9E9F",
  "#A78A6B",
];

function formatMs(ms: number | undefined): string {
  if (ms == null) return "-";
  if (ms >= 1000) return `${(ms / 1000).toFixed(1)}s`;
  return `${Math.round(ms)}ms`;
}

type LatencyAggKind = "avg" | "min" | "max";

function pickStat(
  stats: api.LatencyStats | undefined,
  kind: LatencyAggKind,
): number {
  if (!stats) return 0;
  switch (kind) {
    case "avg": return stats.avgMs ?? 0;
    case "min": return stats.minMs ?? 0;
    case "max": return stats.maxMs ?? 0;
  }
}

function formatTimestamp(ts: string | undefined, bucketSize: string | undefined): string {
  if (!ts) return "";
  // Timestamps are ISO substrings: "2026-03-26" (day), "2026-03-26T13" (hour), "2026-03-26T13:05" (minute)
  // Pad to parseable ISO if needed
  let isoStr = ts;
  if (ts.length === 10) isoStr = ts + "T00:00:00Z";         // day
  else if (ts.length === 13) isoStr = ts + ":00:00Z";       // hour
  else if (ts.length === 16) isoStr = ts + ":00Z";          // minute
  else if (!ts.endsWith("Z")) isoStr = ts + "Z";

  const date = new Date(isoStr);
  if (Number.isNaN(date.getTime())) return ts;

  const day = String(date.getUTCDate()).padStart(2, "0");
  const month = String(date.getUTCMonth() + 1).padStart(2, "0");
  const hour = String(date.getUTCHours()).padStart(2, "0");
  const minute = String(date.getUTCMinutes()).padStart(2, "0");

  if (bucketSize === "day") return `${day}-${month}`;
  if (bucketSize === "minute") return `${day}-${month} ${hour}:${minute}`;
  return `${day}-${month} ${hour}:00`;
}

export default function Metrics() {
  const [data, setData] = useState<api.MetricsOverview | null>(null);
  const [latencyData, setLatencyData] = useState<api.LatencyOverview | null>(
    null,
  );
  const [timeseriesData, setTimeseriesData] = useState<api.TimeSeriesOverview | null>(null);
  const [loading, setLoading] = useState(true);
  const [period, setPeriod] = useState<api.Period>(api.Period._1d);
  const [latencyAgg, setLatencyAgg] = useState<LatencyAggKind>("avg");

  const fetchMetrics = useCallback(async (p: api.Period) => {
    setLoading(true);
    try {
      const client = new api.Client(api.CookieAuth());
      const [result, latency, timeseries] = await Promise.all([
        client.getMetricsOverview(p),
        client.getMetricsLatency(p).catch(() => null),
        client.getMetricsTimeseries(p).catch(() => null),
      ]);
      setData(result);
      setLatencyData(latency);
      setTimeseriesData(timeseries);
    } catch (err) {
      console.error("Failed to fetch metrics", err);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    fetchMetrics(period);
  }, [period, fetchMetrics]);

  function buildStackedData(items: api.EndpointEventTypeMessageCount[]) {
    const byEndpoint = new Map<string, Map<string, number>>();
    const eventTypeSet = new Set<string>();

    for (const item of items) {
      const ep = item.endpointId ?? "";
      const et = item.eventTypeId ?? "unknown";
      eventTypeSet.add(et);
      if (!byEndpoint.has(ep)) {
        byEndpoint.set(ep, new Map());
      }
      byEndpoint.get(ep)!.set(et, (byEndpoint.get(ep)!.get(et) ?? 0) + (item.count ?? 0));
    }

    const types = Array.from(eventTypeSet).sort();
    const rows = Array.from(byEndpoint.entries())
      .map(([endpoint, typesMap]) => {
        const row: Record<string, string | number> = { endpoint };
        let total = 0;
        for (const et of types) {
          const count = typesMap.get(et) ?? 0;
          row[et] = count;
          total += count;
        }
        row._total = total;
        return row;
      })
      .sort((a, b) => (b._total as number) - (a._total as number));

    return { rows, eventTypes: types };
  }

  const published = buildStackedData(data?.published ?? []);
  const handled = buildStackedData(data?.handled ?? []);
  const failed = buildStackedData(data?.failed ?? []);

  const totalHandled = (data?.handled ?? []).reduce((sum, h) => sum + (h.count ?? 0), 0);
  const totalFailed = (data?.failed ?? []).reduce((sum, f) => sum + (f.count ?? 0), 0);
  const totalMessages = totalHandled + totalFailed;
  const failureRate = totalMessages > 0 ? (totalFailed / totalMessages) * 100 : 0;

  // Latency chart data: one row per (endpoint × eventType) with two grouped
  // bars at the selected aggregate — Queue (enqueue → handler entry) and
  // Processing (handler entry → completion). Aggregated server-side in
  // Cosmos; percentiles intentionally not surfaced (would require
  // raw-value scans). Tail-latency monitoring lives with the OTel histograms.
  const latencyChartData = (latencyData?.latencies ?? []).map((l) => ({
    label: `${l.endpointId ?? ""}/${l.eventTypeId ?? ""}`,
    Queue: pickStat(l.queue, latencyAgg),
    Processing: pickStat(l.processing, latencyAgg),
  }));

  const failureRateTone =
    failureRate > 5 ? "danger" : failureRate >= 1 ? "warning" : "default";
  const periodLabel =
    PERIODS.find((p) => p.value === period)?.label ?? "";

  return (
    <Page
      title="Metrics"
      subtitle="Bus-wide throughput, latency, and failure rates"
      actions={
        // Segmented time-range control — matches design's `.seg` look (rec §08).
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
      }
    >
      <div className="flex flex-col w-full gap-5 pb-8">
        {/* KPI tiles — design rec §03. Coloured by tone, not by hue swap. */}
        <StatRow columns={3}>
          <StatTile
            label="Total Messages"
            value={loading ? "—" : totalMessages.toLocaleString()}
            delta={loading ? undefined : `${periodLabel} window`}
            tone="muted"
          />
          <StatTile
            label="Total Failed"
            value={loading ? "—" : totalFailed.toLocaleString()}
            delta={
              loading
                ? undefined
                : totalFailed === 0
                  ? "All clear"
                  : `needs attention · ${periodLabel}`
            }
            tone={totalFailed === 0 ? "default" : "danger"}
          />
          <StatTile
            label="Failure Rate"
            value={loading ? "—" : `${failureRate.toFixed(2)}%`}
            delta={loading ? undefined : `over ${totalMessages.toLocaleString()} msgs`}
            tone={failureRateTone}
          />
        </StatRow>

        {loading ? (
          <div className="flex justify-center items-center py-20">
            <Spinner size="lg" />
          </div>
        ) : (
          <>
            {/* Message Activity Over Time */}
            <Card>
              <CardHeader>
                <CardTitle>Message Activity Over Time</CardTitle>
              </CardHeader>
              <CardContent>
                {(timeseriesData?.dataPoints ?? []).length === 0 ? (
                  <p className="text-muted-foreground text-sm">
                    No activity data in this period.
                  </p>
                ) : (
                  <ResponsiveContainer width="100%" height={300}>
                    <AreaChart
                      data={(timeseriesData?.dataPoints ?? []).map((dp) => ({
                        timestamp: formatTimestamp(dp.timestamp, timeseriesData?.bucketSize),
                        Published: dp.published ?? 0,
                        Handled: dp.handled ?? 0,
                        Failed: dp.failed ?? 0,
                      }))}
                      margin={{ left: 10, right: 20, top: 5, bottom: 5 }}
                    >
                      <CartesianGrid stroke={CHART.grid} strokeDasharray="3 3" />
                      <XAxis
                        dataKey="timestamp"
                        tick={{ fontSize: 11 }}
                        interval="preserveStartEnd"
                      />
                      <YAxis />
                      <Tooltip />
                      <Legend />
                      <Area
                        type="monotone"
                        dataKey="Published"
                        stroke={CHART.published}
                        fill={CHART.published}
                        fillOpacity={0.3}
                      />
                      <Area
                        type="monotone"
                        dataKey="Handled"
                        stroke={CHART.handled}
                        fill={CHART.handled}
                        fillOpacity={0.3}
                      />
                      <Area
                        type="monotone"
                        dataKey="Failed"
                        stroke={CHART.failed}
                        fill={CHART.failed}
                        fillOpacity={0.3}
                      />
                    </AreaChart>
                  </ResponsiveContainer>
                )}
              </CardContent>
            </Card>

            {/* Published Messages */}
            <Card>
              <CardHeader>
                <CardTitle>Published Messages</CardTitle>
              </CardHeader>
              <CardContent>
                {published.rows.length === 0 ? (
                  <p className="text-muted-foreground text-sm">
                    No published messages in this period.
                  </p>
                ) : (
                  <>
                    <ResponsiveContainer
                      width="100%"
                      height={Math.max(200, published.rows.length * 40)}
                    >
                      <BarChart
                        data={published.rows}
                        layout="vertical"
                        margin={{ left: 120, right: 20, top: 5, bottom: 5 }}
                      >
                        <CartesianGrid
                          strokeDasharray="3 3"
                          horizontal={false}
                        />
                        <XAxis type="number" />
                        <YAxis
                          type="category"
                          dataKey="endpoint"
                          width={110}
                          tick={{ fontSize: 12 }}
                        />
                        <Tooltip />
                        <Legend />
                        {published.eventTypes.map((et, i) => (
                          <Bar
                            key={et}
                            dataKey={et}
                            stackId="published"
                            fill={COLORS[i % COLORS.length]}
                            name={et}
                          />
                        ))}
                      </BarChart>
                    </ResponsiveContainer>
                    <table className="w-full mt-4 text-sm">
                      <thead>
                        <tr className="border-b">
                          <th className="text-left py-2 px-3">Endpoint</th>
                          <th className="text-left py-2 px-3">Event Type</th>
                          <th className="text-right py-2 px-3">Count</th>
                        </tr>
                      </thead>
                      <tbody>
                        {(data?.published ?? [])
                          .sort(
                            (a, b) =>
                              (a.endpointId ?? "").localeCompare(
                                b.endpointId ?? "",
                              ) ||
                              (a.eventTypeId ?? "").localeCompare(
                                b.eventTypeId ?? "",
                              ),
                          )
                          .map((p, i) => (
                            <tr key={i} className="border-b">
                              <td className="py-2 px-3">{p.endpointId}</td>
                              <td className="py-2 px-3">{p.eventTypeId}</td>
                              <td className="text-right py-2 px-3">
                                <Badge variant="info" size="sm">
                                  {p.count}
                                </Badge>
                              </td>
                            </tr>
                          ))}
                      </tbody>
                    </table>
                  </>
                )}
              </CardContent>
            </Card>

            {/* Handled Messages */}
            <Card>
              <CardHeader>
                <CardTitle>Handled Messages</CardTitle>
              </CardHeader>
              <CardContent>
                {handled.rows.length === 0 ? (
                  <p className="text-muted-foreground text-sm">
                    No handled messages in this period.
                  </p>
                ) : (
                  <>
                    <ResponsiveContainer
                      width="100%"
                      height={Math.max(200, handled.rows.length * 40)}
                    >
                      <BarChart
                        data={handled.rows}
                        layout="vertical"
                        margin={{ left: 120, right: 20, top: 5, bottom: 5 }}
                      >
                        <CartesianGrid
                          strokeDasharray="3 3"
                          horizontal={false}
                        />
                        <XAxis type="number" />
                        <YAxis
                          type="category"
                          dataKey="endpoint"
                          width={110}
                          tick={{ fontSize: 12 }}
                        />
                        <Tooltip />
                        <Legend />
                        {handled.eventTypes.map((et, i) => (
                          <Bar
                            key={et}
                            dataKey={et}
                            stackId="handled"
                            fill={COLORS[i % COLORS.length]}
                            name={et}
                          />
                        ))}
                      </BarChart>
                    </ResponsiveContainer>
                    <table className="w-full mt-4 text-sm">
                      <thead>
                        <tr className="border-b">
                          <th className="text-left py-2 px-3">Endpoint</th>
                          <th className="text-left py-2 px-3">Event Type</th>
                          <th className="text-right py-2 px-3">Count</th>
                        </tr>
                      </thead>
                      <tbody>
                        {(data?.handled ?? [])
                          .sort(
                            (a, b) =>
                              (a.endpointId ?? "").localeCompare(
                                b.endpointId ?? "",
                              ) ||
                              (a.eventTypeId ?? "").localeCompare(
                                b.eventTypeId ?? "",
                              ),
                          )
                          .map((h, i) => (
                            <tr key={i} className="border-b">
                              <td className="py-2 px-3">{h.endpointId}</td>
                              <td className="py-2 px-3">{h.eventTypeId}</td>
                              <td className="text-right py-2 px-3">
                                <Badge variant="success" size="sm">
                                  {h.count}
                                </Badge>
                              </td>
                            </tr>
                          ))}
                      </tbody>
                    </table>
                  </>
                )}
              </CardContent>
            </Card>

            {/* Failed Messages — happy path renders an EmptyState so negative
                space feels intentional (design rec §09 metrics empty rec). */}
            <Card>
              <CardHeader>
                <CardTitle>Failed Messages</CardTitle>
              </CardHeader>
              <CardContent>
                {failed.rows.length === 0 ? (
                  <EmptyState
                    icon="✓"
                    tone="success"
                    title={`All clear in the last ${periodLabel}`}
                    description="No failed messages in the selected window. Expand the time range to see prior incidents."
                  />
                ) : (
                  <>
                    <ResponsiveContainer
                      width="100%"
                      height={Math.max(200, failed.rows.length * 40)}
                    >
                      <BarChart
                        data={failed.rows}
                        layout="vertical"
                        margin={{ left: 120, right: 20, top: 5, bottom: 5 }}
                      >
                        <CartesianGrid
                          strokeDasharray="3 3"
                          horizontal={false}
                        />
                        <XAxis type="number" />
                        <YAxis
                          type="category"
                          dataKey="endpoint"
                          width={110}
                          tick={{ fontSize: 12 }}
                        />
                        <Tooltip />
                        <Legend />
                        {failed.eventTypes.map((et, i) => (
                          <Bar
                            key={et}
                            dataKey={et}
                            stackId="failed"
                            fill={COLORS[i % COLORS.length]}
                            name={et}
                          />
                        ))}
                      </BarChart>
                    </ResponsiveContainer>
                    <table className="w-full mt-4 text-sm">
                      <thead>
                        <tr className="border-b">
                          <th className="text-left py-2 px-3">Endpoint</th>
                          <th className="text-left py-2 px-3">Event Type</th>
                          <th className="text-right py-2 px-3">Count</th>
                        </tr>
                      </thead>
                      <tbody>
                        {(data?.failed ?? [])
                          .sort(
                            (a, b) =>
                              (a.endpointId ?? "").localeCompare(
                                b.endpointId ?? "",
                              ) ||
                              (a.eventTypeId ?? "").localeCompare(
                                b.eventTypeId ?? "",
                              ),
                          )
                          .map((f, i) => (
                            <tr
                              key={i}
                              className={cn(
                                "border-b",
                                (f.count ?? 0) > 0 &&
                                  "bg-status-danger-50 dark:bg-red-950/30",
                              )}
                            >
                              <td className="py-2 px-3">{f.endpointId}</td>
                              <td className="py-2 px-3">{f.eventTypeId}</td>
                              <td className="text-right py-2 px-3">
                                <Badge
                                  variant={
                                    (f.count ?? 0) > 0 ? "failed" : "default"
                                  }
                                  size="sm"
                                >
                                  {f.count}
                                </Badge>
                              </td>
                            </tr>
                          ))}
                      </tbody>
                    </table>
                  </>
                )}
              </CardContent>
            </Card>

            {/* Latency Breakdown — Queue (enqueue → handler) + Processing (handler → done) */}
            <Card>
              <CardHeader>
                <div className="flex items-center justify-between gap-4 flex-wrap">
                  <div>
                    <CardTitle>Latency Breakdown</CardTitle>
                    <p className="text-xs text-muted-foreground mt-1">
                      Queue (enqueue → handler) + Processing (handler → done). Aggregated from per-message timings; tail-latency lives in the OpenTelemetry histograms.
                    </p>
                  </div>
                  <div className="inline-flex items-center bg-card border border-border rounded-nb-md p-[3px] gap-[2px]">
                    {(["avg", "min", "max"] as LatencyAggKind[]).map((p) => (
                      <button
                        key={p}
                        onClick={() => setLatencyAgg(p)}
                        className={cn(
                          "px-3 py-1.5 rounded-md text-xs font-semibold transition-colors",
                          latencyAgg === p
                            ? "bg-primary text-white"
                            : "text-muted-foreground hover:text-foreground",
                        )}
                      >
                        {p.toUpperCase()}
                      </button>
                    ))}
                  </div>
                </div>
              </CardHeader>
              <CardContent>
                {latencyChartData.length === 0 ? (
                  <p className="text-muted-foreground text-sm">
                    No latency data in this period.
                  </p>
                ) : (
                  <>
                    <ResponsiveContainer
                      width="100%"
                      height={Math.max(200, latencyChartData.length * 50)}
                    >
                      <BarChart
                        data={latencyChartData}
                        layout="vertical"
                        margin={{ left: 160, right: 20, top: 5, bottom: 5 }}
                      >
                        <CartesianGrid
                          strokeDasharray="3 3"
                          horizontal={false}
                        />
                        <XAxis
                          type="number"
                          tickFormatter={(v) => formatMs(v)}
                        />
                        <YAxis
                          type="category"
                          dataKey="label"
                          width={150}
                          tick={{ fontSize: 11 }}
                        />
                        <Tooltip
                          formatter={(v) =>
                            formatMs(typeof v === "number" ? v : 0)
                          }
                        />
                        <Legend />
                        <Bar dataKey="Queue" fill={CHART.published} name="Queue" />
                        <Bar dataKey="Processing" fill={CHART.handled} name="Processing" />
                      </BarChart>
                    </ResponsiveContainer>
                    <table className="w-full mt-4 text-sm">
                      <thead>
                        <tr className="border-b">
                          <th className="text-left py-2 px-3">Endpoint</th>
                          <th className="text-left py-2 px-3">Event Type</th>
                          <th className="text-left py-2 px-3">Metric</th>
                          <th className="text-right py-2 px-3">Count</th>
                          <th className="text-right py-2 px-3">Avg</th>
                          <th className="text-right py-2 px-3">Min</th>
                          <th className="text-right py-2 px-3">Max</th>
                        </tr>
                      </thead>
                      <tbody>
                        {(latencyData?.latencies ?? [])
                          .sort(
                            (a, b) =>
                              (a.endpointId ?? "").localeCompare(
                                b.endpointId ?? "",
                              ) ||
                              (a.eventTypeId ?? "").localeCompare(
                                b.eventTypeId ?? "",
                              ),
                          )
                          .flatMap((l, i) => {
                            // Two rows per (endpoint, eventType) — one per timing series.
                            // First row carries the endpoint/event labels; the second leaves
                            // those cells blank for visual grouping.
                            const stats: { name: string; data: api.LatencyStats | undefined; warn?: boolean }[] = [
                              { name: "Queue", data: l.queue },
                              { name: "Processing", data: l.processing, warn: true },
                            ];
                            return stats.map((s, j) => (
                              <tr key={`${i}-${j}`} className={j === stats.length - 1 ? "border-b" : ""}>
                                <td className="py-1 px-3">{j === 0 ? l.endpointId : ""}</td>
                                <td className="py-1 px-3">{j === 0 ? l.eventTypeId : ""}</td>
                                <td className="py-1 px-3 text-muted-foreground">{s.name}</td>
                                <td className="text-right py-1 px-3">{s.data?.count ?? 0}</td>
                                <td className="text-right py-1 px-3">
                                  <span
                                    className={cn(
                                      s.warn &&
                                        (s.data?.avgMs ?? 0) > 30000 &&
                                        "text-status-danger font-semibold",
                                    )}
                                  >
                                    {formatMs(s.data?.avgMs)}
                                  </span>
                                </td>
                                <td className="text-right py-1 px-3">{formatMs(s.data?.minMs)}</td>
                                <td className="text-right py-1 px-3">{formatMs(s.data?.maxMs)}</td>
                              </tr>
                            ));
                          })}
                      </tbody>
                    </table>
                  </>
                )}
              </CardContent>
            </Card>
          </>
        )}
      </div>
    </Page>
  );
}
