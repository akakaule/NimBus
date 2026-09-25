import * as React from "react";
import * as api from "api-client";
import {
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import {
  ENDPOINT_PALETTE,
  FAILED_STATUSES,
  STATUS_COLORS,
  formatBucket,
} from "functions/failed-messages.functions";

const GRID = "#E5DFCE";
const INK3 = "#8A8473";
// Endpoints beyond this many are folded into "Other" in the per-endpoint split.
const MAX_ENDPOINT_SERIES = 7;
const OTHER = "Other";

export interface FailedHistogramProps {
  histogram: api.FailedHistogram | undefined;
  split: "status" | "endpoint";
  /** ISO start of the selected bar, or "" for none. */
  selectedBucket: string;
  onSelectBucket: (start: string) => void;
  isLoading: boolean;
}

interface Series {
  key: string;
  label: string;
  color: string;
}

type ChartRow = { start: string; total: number } & Record<
  string,
  number | string
>;

export function histogramSeries(
  histogram: api.FailedHistogram | undefined,
  split: "status" | "endpoint",
): Series[] {
  if (split === "status") {
    return FAILED_STATUSES.map((s) => ({
      key: s,
      label: s,
      color: STATUS_COLORS[s],
    }));
  }

  const endpoints = (histogram?.totals?.byEndpoint ?? []).map(
    (e) => e.endpointId ?? "",
  );
  const shown = endpoints.slice(0, MAX_ENDPOINT_SERIES).map((id, i) => ({
    key: `ep:${id}`,
    label: id,
    color: ENDPOINT_PALETTE[i % ENDPOINT_PALETTE.length],
  }));
  return endpoints.length > MAX_ENDPOINT_SERIES
    ? [...shown, { key: `ep:${OTHER}`, label: OTHER, color: INK3 }]
    : shown;
}

export function histogramRows(
  histogram: api.FailedHistogram | undefined,
  split: "status" | "endpoint",
): ChartRow[] {
  const shownEndpoints = new Set(
    (histogram?.totals?.byEndpoint ?? [])
      .slice(0, MAX_ENDPOINT_SERIES)
      .map((e) => e.endpointId ?? ""),
  );
  return (histogram?.buckets ?? []).map((b) => {
    const row: ChartRow = {
      start: b.start?.toISOString() ?? "",
      total: (b.failed ?? 0) + (b.deadLettered ?? 0) + (b.unsupported ?? 0),
    };
    if (split === "status") {
      row.Failed = b.failed ?? 0;
      row.DeadLettered = b.deadLettered ?? 0;
      row.Unsupported = b.unsupported ?? 0;
    } else {
      for (const [endpointId, count] of Object.entries(b.byEndpoint ?? {})) {
        const key = `ep:${shownEndpoints.has(endpointId) ? endpointId : OTHER}`;
        row[key] = ((row[key] as number | undefined) ?? 0) + count;
      }
    }
    return row;
  });
}

/**
 * Stacked bar chart of unresolved failures per time bucket, split by status or endpoint.
 * Clicking a bar selects its window; clicking it again clears the selection.
 */
export default function FailedHistogram({
  histogram,
  split,
  selectedBucket,
  onSelectBucket,
  isLoading,
}: FailedHistogramProps) {
  const series = React.useMemo(
    () => histogramSeries(histogram, split),
    [histogram, split],
  );
  const rows = React.useMemo(
    () => histogramRows(histogram, split),
    [histogram, split],
  );
  const bucketMinutes = histogram?.bucketMinutes ?? 60;
  const selectedTime = selectedBucket
    ? new Date(selectedBucket).getTime()
    : undefined;
  const isSelected = (row: ChartRow) =>
    selectedTime !== undefined &&
    new Date(row.start).getTime() === selectedTime;

  if (!histogram && isLoading) {
    return (
      <div
        className="h-[220px] animate-pulse rounded-md bg-muted"
        aria-label="Loading chart"
      />
    );
  }

  return (
    // Recharts makes its surface focusable; clicking a bar would otherwise ring the whole
    // chart. Keyboard focus keeps its outline.
    <div className="[&_*:focus:not(:focus-visible)]:outline-none">
      <div
        className="mb-2 flex flex-wrap items-center gap-4"
        aria-label="Legend"
      >
        {series.map((s) => (
          <span
            key={s.key}
            className="inline-flex items-center gap-1.5 font-mono text-[11.5px] text-muted-foreground"
          >
            <span
              className="inline-block h-2.5 w-2.5 rounded-sm"
              style={{ background: s.color }}
            />
            {s.label}
          </span>
        ))}
      </div>
      <ResponsiveContainer width="100%" height={220}>
        <BarChart
          data={rows}
          margin={{ top: 8, right: 8, bottom: 0, left: 0 }}
          barCategoryGap="18%"
        >
          <CartesianGrid stroke={GRID} strokeDasharray="3 3" vertical={false} />
          <XAxis
            dataKey="start"
            tickFormatter={(start: string) =>
              formatBucket(new Date(start), bucketMinutes)
            }
            tick={{ fontSize: 10.5, fill: INK3 }}
            stroke={GRID}
            tickLine={false}
            minTickGap={24}
          />
          <YAxis
            allowDecimals={false}
            tick={{ fontSize: 10.5, fill: INK3 }}
            stroke={GRID}
            tickLine={false}
            width={40}
          />
          <Tooltip
            cursor={{ fill: "rgba(232, 116, 60, 0.08)" }}
            content={({ active, payload }) => {
              const row = payload?.[0]?.payload as ChartRow | undefined;
              if (!active || !row) return null;
              return (
                <div className="min-w-[180px] rounded-md border border-border bg-popover px-3 py-2 text-[11.5px] text-popover-foreground shadow-lg">
                  <div className="mb-1 font-mono text-muted-foreground">
                    {formatBucket(new Date(row.start), bucketMinutes, true)}
                  </div>
                  {series.map((s) => (
                    <div
                      key={s.key}
                      className="flex items-center justify-between gap-4"
                    >
                      <span className="inline-flex items-center gap-1.5">
                        <span
                          className="inline-block h-2 w-2 rounded-sm"
                          style={{ background: s.color }}
                        />
                        {s.label}
                      </span>
                      <span className="font-mono font-semibold">
                        {(row[s.key] as number | undefined) ?? 0}
                      </span>
                    </div>
                  ))}
                  <div className="mt-1 border-t border-border pt-1 text-muted-foreground">
                    {row.total > 0
                      ? "Click to show this window"
                      : "No failures"}
                  </div>
                </div>
              );
            }}
          />
          {series.map((s, i) => (
            <Bar
              key={s.key}
              dataKey={s.key}
              stackId="failures"
              fill={s.color}
              isAnimationActive={false}
              cursor="pointer"
              radius={i === series.length - 1 ? [3, 3, 0, 0] : undefined}
              onClick={(_data: unknown, index: number) => {
                const row = rows[index];
                if (!row) return;
                onSelectBucket(isSelected(row) ? "" : row.start);
              }}
            >
              {rows.map((row) => (
                <Cell
                  key={row.start}
                  fillOpacity={
                    selectedTime === undefined || isSelected(row) ? 1 : 0.3
                  }
                />
              ))}
            </Bar>
          ))}
        </BarChart>
      </ResponsiveContainer>
    </div>
  );
}
