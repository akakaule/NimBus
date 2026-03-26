import { useState, useEffect, useCallback } from "react";
import * as api from "api-client";
import Page from "components/page";
import { Card, CardHeader, CardTitle, CardContent } from "components/ui/card";
import { Spinner } from "components/ui/spinner";
import { Badge } from "components/ui/badge";
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

const PERIODS: { label: string; value: api.Period }[] = [
  { label: "1h", value: api.Period._1h },
  { label: "12h", value: api.Period._12h },
  { label: "1d", value: api.Period._1d },
  { label: "3d", value: api.Period._3d },
  { label: "7d", value: api.Period._7d },
  { label: "30d", value: api.Period._30d },
];

const COLORS = [
  "#3b82f6",
  "#10b981",
  "#f59e0b",
  "#ef4444",
  "#8b5cf6",
  "#ec4899",
  "#06b6d4",
  "#84cc16",
];

function formatMs(ms: number | undefined): string {
  if (ms == null) return "-";
  if (ms >= 1000) return `${(ms / 1000).toFixed(1)}s`;
  return `${Math.round(ms)}ms`;
}

const metricsAxisDateTime = new Intl.DateTimeFormat("da-DK", {
  timeZone: "Europe/Copenhagen",
  day: "2-digit",
  month: "2-digit",
  hour: "2-digit",
  hour12: false,
});

function formatTimestamp(ts: string | undefined, bucketSize: string | undefined): string {
  if (!ts) return "";
  const date = new Date(ts);
  if (Number.isNaN(date.getTime())) return ts;

  const parts = metricsAxisDateTime.formatToParts(date);
  const values = Object.fromEntries(parts.map((part) => [part.type, part.value]));

  if (bucketSize === "minute") {
    return `${values.day}-${values.month} ${values.hour}`;
  }

  return `${values.day}-${values.month} ${values.hour}`;
}

export default function Metrics() {
  const [data, setData] = useState<api.MetricsOverview | null>(null);
  const [latencyData, setLatencyData] = useState<api.LatencyOverview | null>(
    null,
  );
  const [timeseriesData, setTimeseriesData] = useState<api.TimeSeriesOverview | null>(null);
  const [loading, setLoading] = useState(true);
  const [period, setPeriod] = useState<api.Period>(api.Period._1d);

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

  // Latency chart data: group by event type, show P50/P95/P99
  const latencyChartData = (latencyData?.latencies ?? []).map((l) => ({
    label: `${l.endpointId ?? ""}/${l.eventTypeId ?? ""}`,
    P50: l.p50LatencyMs ?? 0,
    P95: l.p95LatencyMs ?? 0,
    P99: l.p99LatencyMs ?? 0,
  }));

  return (
    <Page title="Metrics">
      <div className="flex flex-col w-full gap-6 pb-8">
        {/* Period selector */}
        <div className="flex gap-2">
          {PERIODS.map((p) => (
            <button
              key={p.value}
              onClick={() => setPeriod(p.value)}
              className={`px-4 py-2 text-sm font-semibold rounded-md border transition-colors ${
                period === p.value
                  ? "bg-primary text-white border-primary"
                  : "bg-card text-foreground border-border hover:bg-accent"
              }`}
            >
              {p.label}
            </button>
          ))}
        </div>

        {/* KPI Summary Cards */}
        <div className="grid grid-cols-3 gap-4">
          <Card>
            <CardContent className="pt-6 text-center">
              <p className="text-sm text-muted-foreground">Total Messages</p>
              <p className="text-3xl font-bold mt-1">
                {loading ? "-" : totalMessages.toLocaleString()}
              </p>
            </CardContent>
          </Card>
          <Card>
            <CardContent className="pt-6 text-center">
              <p className="text-sm text-muted-foreground">Total Failed</p>
              <p className={`text-3xl font-bold mt-1 ${totalFailed > 0 && !loading ? "text-red-600" : ""}`}>
                {loading ? "-" : totalFailed.toLocaleString()}
              </p>
            </CardContent>
          </Card>
          <Card>
            <CardContent className="pt-6 text-center">
              <p className="text-sm text-muted-foreground">Failure Rate</p>
              <p className={`text-3xl font-bold mt-1 ${
                loading ? "" :
                failureRate > 5 ? "text-red-600" :
                failureRate >= 1 ? "text-yellow-600" :
                "text-green-600"
              }`}>
                {loading ? "-" : `${failureRate.toFixed(2)}%`}
              </p>
            </CardContent>
          </Card>
        </div>

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
                      <CartesianGrid strokeDasharray="3 3" />
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
                        stroke="#3b82f6"
                        fill="#3b82f6"
                        fillOpacity={0.3}
                      />
                      <Area
                        type="monotone"
                        dataKey="Handled"
                        stroke="#10b981"
                        fill="#10b981"
                        fillOpacity={0.3}
                      />
                      <Area
                        type="monotone"
                        dataKey="Failed"
                        stroke="#ef4444"
                        fill="#ef4444"
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
                                <Badge variant="info">{p.count}</Badge>
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
                                <Badge variant="success">{h.count}</Badge>
                              </td>
                            </tr>
                          ))}
                      </tbody>
                    </table>
                  </>
                )}
              </CardContent>
            </Card>

            {/* Failed Messages */}
            <Card>
              <CardHeader>
                <CardTitle>Failed Messages</CardTitle>
              </CardHeader>
              <CardContent>
                {failed.rows.length === 0 ? (
                  <p className="text-muted-foreground text-sm">
                    No failed messages in this period.
                  </p>
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
                              className={`border-b ${(f.count ?? 0) > 0 ? "bg-red-50" : ""}`}
                            >
                              <td className="py-2 px-3">{f.endpointId}</td>
                              <td className="py-2 px-3">{f.eventTypeId}</td>
                              <td className="text-right py-2 px-3">
                                <Badge
                                  variant={
                                    (f.count ?? 0) > 0 ? "error" : "default"
                                  }
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

            {/* End-to-End Latency */}
            <Card>
              <CardHeader>
                <CardTitle>End-to-End Latency</CardTitle>
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
                        <Bar dataKey="P50" fill="#3b82f6" name="P50" />
                        <Bar dataKey="P95" fill="#f59e0b" name="P95" />
                        <Bar dataKey="P99" fill="#ef4444" name="P99" />
                      </BarChart>
                    </ResponsiveContainer>
                    <table className="w-full mt-4 text-sm">
                      <thead>
                        <tr className="border-b">
                          <th className="text-left py-2 px-3">Endpoint</th>
                          <th className="text-left py-2 px-3">Event Type</th>
                          <th className="text-right py-2 px-3">Count</th>
                          <th className="text-right py-2 px-3">Avg</th>
                          <th className="text-right py-2 px-3">P50</th>
                          <th className="text-right py-2 px-3">P95</th>
                          <th className="text-right py-2 px-3">P99</th>
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
                          .map((l, i) => (
                            <tr key={i} className="border-b">
                              <td className="py-2 px-3">{l.endpointId}</td>
                              <td className="py-2 px-3">{l.eventTypeId}</td>
                              <td className="text-right py-2 px-3">
                                {l.count}
                              </td>
                              <td className="text-right py-2 px-3">
                                {formatMs(l.avgLatencyMs)}
                              </td>
                              <td className="text-right py-2 px-3">
                                {formatMs(l.p50LatencyMs)}
                              </td>
                              <td className="text-right py-2 px-3">
                                <span
                                  className={
                                    (l.p95LatencyMs ?? 0) > 30000
                                      ? "text-red-600 font-semibold"
                                      : ""
                                  }
                                >
                                  {formatMs(l.p95LatencyMs)}
                                </span>
                              </td>
                              <td className="text-right py-2 px-3">
                                {formatMs(l.p99LatencyMs)}
                              </td>
                              <td className="text-right py-2 px-3">
                                {formatMs(l.maxLatencyMs)}
                              </td>
                            </tr>
                          ))}
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
