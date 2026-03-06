import { useState, useEffect, useCallback } from "react";
import * as api from "api-client";
import Page from "components/page";
import { Card, CardHeader, CardTitle, CardContent } from "components/ui/card";
import { Spinner } from "components/ui/spinner";
import { Badge } from "components/ui/badge";
import {
  BarChart,
  Bar,
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

export default function Metrics() {
  const [data, setData] = useState<api.MetricsOverview | null>(null);
  const [latencyData, setLatencyData] = useState<api.LatencyOverview | null>(
    null,
  );
  const [loading, setLoading] = useState(true);
  const [period, setPeriod] = useState<api.Period>(api.Period._1d);

  const fetchMetrics = useCallback(async (p: api.Period) => {
    setLoading(true);
    try {
      const client = new api.Client(api.CookieAuth());
      const [result, latency] = await Promise.all([
        client.getMetricsOverview(p),
        client.getMetricsLatency(p).catch(() => null),
      ]);
      setData(result);
      setLatencyData(latency);
    } catch (err) {
      console.error("Failed to fetch metrics", err);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    fetchMetrics(period);
  }, [period, fetchMetrics]);

  const publishedChartData = (data?.published ?? [])
    .sort((a, b) => (b.count ?? 0) - (a.count ?? 0))
    .map((p) => ({
      endpoint: p.endpointId ?? "",
      count: p.count ?? 0,
    }));

  const failedChartData = (data?.failed ?? [])
    .sort((a, b) => (b.count ?? 0) - (a.count ?? 0))
    .map((f) => ({
      endpoint: f.endpointId ?? "",
      count: f.count ?? 0,
    }));

  // Group handled by endpoint with stacked event types
  const handledByEndpoint = new Map<string, Map<string, number>>();
  const allEventTypes = new Set<string>();

  for (const h of data?.handled ?? []) {
    const ep = h.endpointId ?? "";
    const et = h.eventTypeId ?? "unknown";
    allEventTypes.add(et);
    if (!handledByEndpoint.has(ep)) {
      handledByEndpoint.set(ep, new Map());
    }
    handledByEndpoint.get(ep)!.set(et, h.count ?? 0);
  }

  const eventTypes = Array.from(allEventTypes).sort();
  const handledChartData = Array.from(handledByEndpoint.entries())
    .map(([endpoint, types]) => {
      const row: Record<string, string | number> = { endpoint };
      let total = 0;
      for (const et of eventTypes) {
        const count = types.get(et) ?? 0;
        row[et] = count;
        total += count;
      }
      row._total = total;
      return row;
    })
    .sort((a, b) => (b._total as number) - (a._total as number));

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

        {loading ? (
          <div className="flex justify-center items-center py-20">
            <Spinner size="lg" />
          </div>
        ) : (
          <>
            {/* Published Messages */}
            <Card>
              <CardHeader>
                <CardTitle>Published Messages</CardTitle>
              </CardHeader>
              <CardContent>
                {publishedChartData.length === 0 ? (
                  <p className="text-muted-foreground text-sm">
                    No published messages in this period.
                  </p>
                ) : (
                  <>
                    <ResponsiveContainer
                      width="100%"
                      height={Math.max(200, publishedChartData.length * 40)}
                    >
                      <BarChart
                        data={publishedChartData}
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
                        <Bar dataKey="count" fill="#3b82f6" name="Published" />
                      </BarChart>
                    </ResponsiveContainer>
                    <table className="w-full mt-4 text-sm">
                      <thead>
                        <tr className="border-b">
                          <th className="text-left py-2 px-3">Endpoint</th>
                          <th className="text-right py-2 px-3">Count</th>
                        </tr>
                      </thead>
                      <tbody>
                        {publishedChartData.map((row) => (
                          <tr key={row.endpoint} className="border-b">
                            <td className="py-2 px-3">{row.endpoint}</td>
                            <td className="text-right py-2 px-3">
                              <Badge variant="info">{row.count}</Badge>
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
                {handledChartData.length === 0 ? (
                  <p className="text-muted-foreground text-sm">
                    No handled messages in this period.
                  </p>
                ) : (
                  <>
                    <ResponsiveContainer
                      width="100%"
                      height={Math.max(200, handledChartData.length * 40)}
                    >
                      <BarChart
                        data={handledChartData}
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
                        {eventTypes.map((et, i) => (
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
                {failedChartData.length === 0 ? (
                  <p className="text-muted-foreground text-sm">
                    No failed messages in this period.
                  </p>
                ) : (
                  <>
                    <ResponsiveContainer
                      width="100%"
                      height={Math.max(200, failedChartData.length * 40)}
                    >
                      <BarChart
                        data={failedChartData}
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
                        <Bar dataKey="count" fill="#ef4444" name="Failed" />
                      </BarChart>
                    </ResponsiveContainer>
                    <table className="w-full mt-4 text-sm">
                      <thead>
                        <tr className="border-b">
                          <th className="text-left py-2 px-3">Endpoint</th>
                          <th className="text-right py-2 px-3">Count</th>
                        </tr>
                      </thead>
                      <tbody>
                        {failedChartData.map((row) => (
                          <tr
                            key={row.endpoint}
                            className={`border-b ${row.count > 0 ? "bg-red-50" : ""}`}
                          >
                            <td className="py-2 px-3">{row.endpoint}</td>
                            <td className="text-right py-2 px-3">
                              <Badge
                                variant={row.count > 0 ? "error" : "default"}
                              >
                                {row.count}
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
