import React, { useState, useEffect, useCallback } from "react";
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

export default function Insights() {
  const [data, setData] = useState<api.FailedInsightsOverview | null>(null);
  const [loading, setLoading] = useState(true);
  const [period, setPeriod] = useState<api.Period>(api.Period._1d);
  const [expanded, setExpanded] = useState<Set<number>>(new Set());

  const toggleExpand = (idx: number) => {
    setExpanded((prev) => {
      const next = new Set(prev);
      next.has(idx) ? next.delete(idx) : next.add(idx);
      return next;
    });
  };

  const fetchData = useCallback(async (p: api.Period) => {
    setLoading(true);
    try {
      const client = new api.Client(api.CookieAuth());
      const result = await client.getMetricsFailedInsights(p);
      setData(result);
    } catch (err) {
      console.error("Failed to fetch insights", err);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    fetchData(period);
  }, [period, fetchData]);

  const groups = data?.groups ?? [];

  const chartData = groups.slice(0, 15).map((g) => ({
    category:
      (g.errorCategory ?? "").length > 40
        ? (g.errorCategory ?? "").substring(0, 40) + "..."
        : g.errorCategory ?? "",
    count: g.count ?? 0,
  }));

  return (
    <Page title="Failed Message Insights">
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
          {data && !loading && (
            <span className="flex items-center ml-4 text-sm text-muted-foreground">
              Total failed: {data.totalFailed ?? 0}
            </span>
          )}
        </div>

        {loading ? (
          <div className="flex justify-center items-center py-20">
            <Spinner size="lg" />
          </div>
        ) : (
          <>
            {/* Error Pattern Chart */}
            <Card>
              <CardHeader>
                <CardTitle>Top Error Patterns</CardTitle>
              </CardHeader>
              <CardContent>
                {chartData.length === 0 ? (
                  <p className="text-muted-foreground text-sm">
                    No failed messages in this period.
                  </p>
                ) : (
                  <ResponsiveContainer
                    width="100%"
                    height={Math.max(200, chartData.length * 40)}
                  >
                    <BarChart
                      data={chartData}
                      layout="vertical"
                      margin={{ left: 200, right: 20, top: 5, bottom: 5 }}
                    >
                      <CartesianGrid
                        strokeDasharray="3 3"
                        horizontal={false}
                      />
                      <XAxis type="number" />
                      <YAxis
                        type="category"
                        dataKey="category"
                        width={190}
                        tick={{ fontSize: 11 }}
                      />
                      <Tooltip />
                      <Bar dataKey="count" fill="#ef4444" name="Failed" />
                    </BarChart>
                  </ResponsiveContainer>
                )}
              </CardContent>
            </Card>

            {/* Error Pattern Table */}
            <Card>
              <CardHeader>
                <CardTitle>Error Pattern Details</CardTitle>
              </CardHeader>
              <CardContent>
                {groups.length === 0 ? (
                  <p className="text-muted-foreground text-sm">
                    No failed messages in this period.
                  </p>
                ) : (
                  <div className="overflow-x-auto">
                    <table className="w-full text-sm">
                      <thead>
                        <tr className="border-b">
                          <th className="text-left py-2 px-3">
                            Error Category
                          </th>
                          <th className="text-right py-2 px-3">Count</th>
                          <th className="text-left py-2 px-3">Endpoints</th>
                          <th className="text-left py-2 px-3">Event Types</th>
                          <th className="text-left py-2 px-3">
                            Latest Occurrence
                          </th>
                          <th className="text-left py-2 px-3">
                            Example Error
                          </th>
                        </tr>
                      </thead>
                      <tbody>
                        {groups.map((g, i) => {
                          const subs = g.subGroups ?? [];
                          const hasSubGroups = subs.length > 1;
                          const isExpanded = expanded.has(i);

                          return (
                            <React.Fragment key={i}>
                              <tr
                                className={`border-b ${hasSubGroups ? "cursor-pointer hover:bg-accent" : ""}`}
                                onClick={
                                  hasSubGroups
                                    ? () => toggleExpand(i)
                                    : undefined
                                }
                              >
                                <td
                                  className="py-2 px-3 font-mono text-xs max-w-xs truncate"
                                  title={g.errorCategory ?? ""}
                                >
                                  <span className="inline-flex items-center gap-1">
                                    {hasSubGroups && (
                                      <span className="text-muted-foreground text-[10px] w-3">
                                        {isExpanded ? "\u25BC" : "\u25B6"}
                                      </span>
                                    )}
                                    {g.errorCategory}
                                    {hasSubGroups && (
                                      <span className="text-muted-foreground text-[10px] ml-1">
                                        ({subs.length} patterns)
                                      </span>
                                    )}
                                  </span>
                                </td>
                                <td className="text-right py-2 px-3">
                                  <Badge variant="error">{g.count}</Badge>
                                </td>
                                <td className="py-2 px-3">
                                  <div className="flex flex-wrap gap-1">
                                    {(g.endpoints ?? []).map((ep) => (
                                      <Badge key={ep} variant="default">
                                        {ep}
                                      </Badge>
                                    ))}
                                  </div>
                                </td>
                                <td className="py-2 px-3">
                                  <div className="flex flex-wrap gap-1">
                                    {(g.eventTypes ?? []).map((et) => (
                                      <Badge key={et} variant="info">
                                        {et}
                                      </Badge>
                                    ))}
                                  </div>
                                </td>
                                <td className="py-2 px-3 whitespace-nowrap">
                                  {g.latestOccurrence
                                    ? new Date(
                                        g.latestOccurrence.toString(),
                                      ).toLocaleString()
                                    : "-"}
                                </td>
                                <td
                                  className="py-2 px-3 max-w-sm truncate text-xs text-muted-foreground"
                                  title={g.exampleErrorText ?? ""}
                                >
                                  {g.exampleErrorText}
                                </td>
                              </tr>
                              {isExpanded &&
                                subs.map((sg, j) => (
                                  <tr
                                    key={`${i}-${j}`}
                                    className="border-b bg-muted/50"
                                  >
                                    <td
                                      className="py-2 px-3 pl-8 font-mono text-xs max-w-xs truncate text-muted-foreground"
                                      title={sg.normalizedPattern ?? ""}
                                    >
                                      {sg.normalizedPattern}
                                    </td>
                                    <td className="text-right py-2 px-3">
                                      <Badge variant="error">
                                        {sg.count}
                                      </Badge>
                                    </td>
                                    <td className="py-2 px-3">
                                      <div className="flex flex-wrap gap-1">
                                        {(sg.endpoints ?? []).map((ep) => (
                                          <Badge key={ep} variant="default">
                                            {ep}
                                          </Badge>
                                        ))}
                                      </div>
                                    </td>
                                    <td className="py-2 px-3">
                                      <div className="flex flex-wrap gap-1">
                                        {(sg.eventTypes ?? []).map((et) => (
                                          <Badge key={et} variant="info">
                                            {et}
                                          </Badge>
                                        ))}
                                      </div>
                                    </td>
                                    <td className="py-2 px-3 whitespace-nowrap">
                                      {sg.latestOccurrence
                                        ? new Date(
                                            sg.latestOccurrence.toString(),
                                          ).toLocaleString()
                                        : "-"}
                                    </td>
                                    <td
                                      className="py-2 px-3 max-w-sm truncate text-xs text-muted-foreground"
                                      title={sg.exampleErrorText ?? ""}
                                    >
                                      {sg.exampleErrorText}
                                    </td>
                                  </tr>
                                ))}
                            </React.Fragment>
                          );
                        })}
                      </tbody>
                    </table>
                  </div>
                )}
              </CardContent>
            </Card>
          </>
        )}
      </div>
    </Page>
  );
}
