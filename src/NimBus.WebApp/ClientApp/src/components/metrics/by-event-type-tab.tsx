import { useMemo, useRef, useState } from "react";
import * as api from "api-client";
import { Combobox, type ComboboxOption } from "components/ui/combobox";
import { EmptyState } from "components/ui/empty-state";
import { Spinner } from "components/ui/spinner";
import { useTheme } from "hooks/use-theme";
import { cn } from "lib/utils";
import {
  CartesianGrid,
  Line,
  LineChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";

// Categorical palettes, one hue per event type in fixed order (blue, amber,
// green, purple, teal). Both validated with the dataviz palette checker
// against the card surfaces (#FAF8F2 light / #1A1814 dark); the dark set
// re-steps amber and purple into the dark lightness band rather than
// reusing the light hex values.
const SERIES_PALETTE_LIGHT = [
  "#3A6FB0",
  "#C98A1B",
  "#2E8F5E",
  "#6B3FA3",
  "#0F8FA3",
] as const;
const SERIES_PALETTE_DARK = [
  "#3A6FB0",
  "#C4861A",
  "#2E8F5E",
  "#7B4FB5",
  "#0F8FA3",
] as const;

/** Chart shows at most this many series — one per palette hue, never cycled. */
export const MAX_CHART_SERIES = 5;

const CHART_INK = { light: "#8A8473", dark: "#A8A293" } as const;
const CHART_GRID = { light: "#E5DFCE", dark: "#2A2620" } as const;

// -------------------------------------------------------------------------
// Pure helpers (exported for tests)
// -------------------------------------------------------------------------

/** Bucket keys are truncated ISO timestamps: 16 chars = minute, 13 = hour,
    10 = day — the contract shared by both store backends. */
export function bucketKeyToDate(key: string): Date | null {
  let iso = key;
  if (key.length === 10) iso = key + "T00:00:00Z";
  else if (key.length === 13) iso = key + ":00:00Z";
  else if (key.length === 16) iso = key + ":00Z";
  else if (!key.endsWith("Z")) iso = key + "Z";
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? null : d;
}

/** True when the rendered window covers more than 24 hours — hour ticks are
    ambiguous then ("14:00" of which day?), so labels carry the date too. */
export function spansMoreThanOneDay(keys: (string | undefined)[]): boolean {
  const times = keys
    .filter((k): k is string => Boolean(k))
    .map((k) => bucketKeyToDate(k)?.getTime())
    .filter((t): t is number => t !== undefined);
  if (times.length < 2) return false;
  return Math.max(...times) - Math.min(...times) > 86_400_000;
}

export function formatBucketLabel(
  key: string | undefined,
  bucketSize: string | undefined,
  withDate = false,
): string {
  if (!key) return "";
  const d = bucketKeyToDate(key);
  if (!d) return key;
  const day = String(d.getDate()).padStart(2, "0");
  const mon = String(d.getMonth() + 1).padStart(2, "0");
  const hr = String(d.getHours()).padStart(2, "0");
  const min = String(d.getMinutes()).padStart(2, "0");
  if (bucketSize === "day") return `${day}/${mon}`;
  if (bucketSize === "minute") return `${hr}:${min}`;
  return withDate ? `${day}/${mon} ${hr}:00` : `${hr}:00`;
}

/** Fill the gaps between the earliest and latest observed bucket keys so the
    x-axis is a continuous time grid even though the API returns sparse
    buckets. Capped defensively — the realistic maximum is 168 (7d hourly). */
export function buildBucketGrid(
  observedKeys: string[],
  bucketSize: string | undefined,
): string[] {
  const keys = [...new Set(observedKeys)].sort();
  if (keys.length <= 1) return keys;

  const stepMs =
    bucketSize === "minute"
      ? 60_000
      : bucketSize === "day"
        ? 86_400_000
        : 3_600_000;
  const keyLength = keys[0].length;
  const start = bucketKeyToDate(keys[0]);
  const end = bucketKeyToDate(keys[keys.length - 1]);
  if (!start || !end) return keys;

  const grid: string[] = [];
  for (
    let t = start.getTime();
    t <= end.getTime() && grid.length < 2000;
    t += stepMs
  ) {
    grid.push(new Date(t).toISOString().slice(0, keyLength));
  }
  // Union with the observed keys so an unexpected key shape never drops data.
  return [...new Set([...grid, ...keys])].sort();
}

/** The series to draw: the explicit selection when there is one (first
    MAX_CHART_SERIES of it), otherwise the top N by total. Server pre-sorts
    by total desc. */
export function pickVisibleSeries(
  series: api.EventTypeSeries[],
  selectedTypes: string[],
): api.EventTypeSeries[] {
  if (selectedTypes.length > 0) {
    return series
      .filter((s) => s.eventTypeId && selectedTypes.includes(s.eventTypeId))
      .slice(0, MAX_CHART_SERIES);
  }
  return series.slice(0, MAX_CHART_SERIES);
}

export interface ChartRow {
  ts: string;
  [eventTypeId: string]: string | number;
}

/** One recharts row per grid bucket, a zero-filled count column per series. */
export function buildChartRows(
  visible: api.EventTypeSeries[],
  bucketSize: string | undefined,
): ChartRow[] {
  const observed = visible.flatMap(
    (s) => s.dataPoints?.map((p) => p.timestamp ?? "").filter(Boolean) ?? [],
  );
  const grid = buildBucketGrid(observed, bucketSize);
  const bySeries = visible.map((s) => {
    const m = new Map<string, number>();
    for (const p of s.dataPoints ?? []) {
      if (p.timestamp) m.set(p.timestamp, p.published ?? 0);
    }
    return m;
  });
  return grid.map((ts) => {
    const row: ChartRow = { ts };
    visible.forEach((s, i) => {
      row[s.eventTypeId ?? ""] = bySeries[i].get(ts) ?? 0;
    });
    return row;
  });
}

export interface EventTypeTableRow {
  eventTypeId: string;
  published: number;
  handled: number;
  failed: number;
  /** 0..1 — share of all published messages in the window. */
  share: number;
}

/** Join the published series totals with handled / failed sums from the
    overview payload (already fetched for the Overview tab). */
export function buildTableRows(
  series: api.EventTypeSeries[],
  overview: api.MetricsOverview | null,
): EventTypeTableRow[] {
  const sumByType = (
    items: api.EndpointEventTypeMessageCount[] | undefined,
  ) => {
    const m = new Map<string, number>();
    for (const item of items ?? []) {
      if (!item.eventTypeId) continue;
      m.set(
        item.eventTypeId,
        (m.get(item.eventTypeId) ?? 0) + (item.count ?? 0),
      );
    }
    return m;
  };
  const handled = sumByType(overview?.handled);
  const failed = sumByType(overview?.failed);
  const totalPublished = series.reduce((s, x) => s + (x.total ?? 0), 0);

  return series
    .filter((s) => s.eventTypeId)
    .map((s) => ({
      eventTypeId: s.eventTypeId ?? "",
      published: s.total ?? 0,
      handled: handled.get(s.eventTypeId ?? "") ?? 0,
      failed: failed.get(s.eventTypeId ?? "") ?? 0,
      share: totalPublished > 0 ? (s.total ?? 0) / totalPublished : 0,
    }));
}

// -------------------------------------------------------------------------
// Component
// -------------------------------------------------------------------------

interface ByEventTypeTabProps {
  data: api.EventTypeTimeSeriesOverview | null;
  overview: api.MetricsOverview | null;
  periodLabel: string;
  loading: boolean;
  selectedTypes: string[];
  onSelectedTypesChange: (types: string[]) => void;
}

type SortColumn = "published" | "handled" | "failed";

export default function ByEventTypeTab({
  data,
  overview,
  periodLabel,
  loading,
  selectedTypes,
  onSelectedTypesChange,
}: ByEventTypeTabProps) {
  const { resolvedTheme } = useTheme();
  const palette =
    resolvedTheme === "dark" ? SERIES_PALETTE_DARK : SERIES_PALETTE_LIGHT;
  const ink = CHART_INK[resolvedTheme];
  const grid = CHART_GRID[resolvedTheme];

  const [search, setSearch] = useState("");
  const [sortCol, setSortCol] = useState<SortColumn>("published");
  const [sortDesc, setSortDesc] = useState(true);

  const series = useMemo(() => data?.series ?? [], [data]);
  const visible = useMemo(
    () => pickVisibleSeries(series, selectedTypes),
    [series, selectedTypes],
  );

  // Color follows the event type, not its position: an event type keeps its
  // hue while it stays visible, and freed hues go to newcomers — so removing
  // one series from the filter never repaints the survivors.
  const colorMapRef = useRef(new Map<string, number>());
  const colorOf = useMemo(() => {
    const assigned = colorMapRef.current;
    const visibleIds = visible.map((s) => s.eventTypeId ?? "");
    for (const id of [...assigned.keys()]) {
      if (!visibleIds.includes(id)) assigned.delete(id);
    }
    const used = new Set(assigned.values());
    for (const id of visibleIds) {
      if (assigned.has(id)) continue;
      for (let i = 0; i < palette.length; i++) {
        if (!used.has(i)) {
          assigned.set(id, i);
          used.add(i);
          break;
        }
      }
    }
    return (id: string) => palette[assigned.get(id) ?? 0];
  }, [visible, palette]);

  const chartRows = useMemo(
    () => buildChartRows(visible, data?.bucketSize),
    [visible, data?.bucketSize],
  );

  // Hour ticks are ambiguous once the rendered window crosses a day — carry
  // the date on axis and tooltip labels then.
  const ticksWithDate = useMemo(
    () => spansMoreThanOneDay(chartRows.map((r) => r.ts)),
    [chartRows],
  );

  const filterOptions: ComboboxOption[] = useMemo(
    () =>
      series
        .filter((s) => s.eventTypeId)
        .map((s) => ({
          value: s.eventTypeId ?? "",
          label: `${s.eventTypeId} (${(s.total ?? 0).toLocaleString()})`,
        })),
    [series],
  );

  const tableRows = useMemo(() => {
    let rows = buildTableRows(series, overview);
    if (selectedTypes.length > 0) {
      rows = rows.filter((r) => selectedTypes.includes(r.eventTypeId));
    }
    if (search.trim()) {
      const q = search.trim().toLowerCase();
      rows = rows.filter((r) => r.eventTypeId.toLowerCase().includes(q));
    }
    const dir = sortDesc ? -1 : 1;
    return [...rows].sort((a, b) => dir * (a[sortCol] - b[sortCol]));
  }, [series, overview, selectedTypes, search, sortCol, sortDesc]);

  const onSort = (col: SortColumn) => {
    if (col === sortCol) setSortDesc((d) => !d);
    else {
      setSortCol(col);
      setSortDesc(true);
    }
  };

  if (loading) {
    return (
      <div className="flex justify-center items-center py-20">
        <Spinner size="lg" />
      </div>
    );
  }

  if (series.length === 0) {
    return (
      <EmptyState
        icon="—"
        title="No published messages in this window"
        description="Counts appear as soon as endpoints publish events. Try a wider time range."
      />
    );
  }

  return (
    <div className="flex flex-col w-full gap-4 pb-8">
      <div className="max-w-xl">
        <Combobox
          multiple
          options={filterOptions}
          value={selectedTypes}
          onChange={onSelectedTypesChange}
          placeholder="Filter event types…"
          label="Event types"
        />
      </div>

      <div className="bg-card border border-border rounded-nb-md p-4">
        <div className="flex items-baseline justify-between gap-4 mb-2 flex-wrap">
          <h4 className="m-0 text-sm font-bold tracking-tight">
            Published by event type
          </h4>
          <span className="font-mono text-[11px] text-muted-foreground">
            {selectedTypes.length > 0
              ? `${visible.length} selected · ${periodLabel}`
              : `top ${visible.length} by volume · ${periodLabel}`}
          </span>
        </div>

        <div className="flex items-center gap-3 flex-wrap font-mono text-[11px] text-muted-foreground mb-2">
          {visible.map((s) => (
            <span
              key={s.eventTypeId}
              className="inline-flex items-center gap-1.5"
            >
              <span
                aria-hidden="true"
                className="inline-block w-2.5 h-[3px]"
                style={{ background: colorOf(s.eventTypeId ?? "") }}
              />
              {s.eventTypeId}
            </span>
          ))}
        </div>

        {selectedTypes.length > MAX_CHART_SERIES && (
          <p className="m-0 mb-2 text-[11.5px] text-muted-foreground">
            Charting the first {MAX_CHART_SERIES} of {selectedTypes.length}{" "}
            selected event types — the table below covers all of them.
          </p>
        )}

        {chartRows.length === 0 ? (
          <EmptyState
            icon="—"
            title="No traffic for this selection"
            description="None of the selected event types were published in this window."
          />
        ) : (
          <ResponsiveContainer width="100%" height={280}>
            <LineChart
              data={chartRows}
              margin={{ top: 8, right: 16, bottom: 0, left: 0 }}
            >
              <CartesianGrid
                stroke={grid}
                strokeDasharray="3 3"
                vertical={false}
              />
              <XAxis
                dataKey="ts"
                tickFormatter={(ts: string) =>
                  formatBucketLabel(ts, data?.bucketSize, ticksWithDate)
                }
                tick={{ fontSize: 10.5, fill: ink }}
                stroke={grid}
                tickLine={false}
                minTickGap={32}
              />
              <YAxis
                allowDecimals={false}
                tick={{ fontSize: 10.5, fill: ink }}
                stroke={grid}
                tickLine={false}
                width={44}
              />
              <Tooltip
                content={({ active, payload, label }) => {
                  if (!active || !payload?.length) return null;
                  const entries = [...payload].sort(
                    (a, b) => (Number(b.value) || 0) - (Number(a.value) || 0),
                  );
                  return (
                    <div className="bg-popover text-popover-foreground border border-border rounded-md shadow-lg px-3 py-2 text-[11.5px]">
                      <div className="font-mono text-muted-foreground mb-1">
                        {formatBucketLabel(
                          String(label),
                          data?.bucketSize,
                          ticksWithDate,
                        )}
                      </div>
                      {entries.map((e) => (
                        <div
                          key={String(e.dataKey)}
                          className="flex items-center gap-1.5"
                        >
                          <span
                            aria-hidden="true"
                            className="inline-block w-2 h-2 rounded-sm"
                            style={{ background: e.color }}
                          />
                          <span className="truncate max-w-[220px]">
                            {String(e.dataKey)}
                          </span>
                          <span className="ml-auto pl-3 font-mono font-bold tabular-nums">
                            {(Number(e.value) || 0).toLocaleString()}
                          </span>
                        </div>
                      ))}
                    </div>
                  );
                }}
              />
              {visible.map((s) => (
                <Line
                  key={s.eventTypeId}
                  type="monotone"
                  dataKey={s.eventTypeId ?? ""}
                  stroke={colorOf(s.eventTypeId ?? "")}
                  strokeWidth={2}
                  dot={false}
                  activeDot={{ r: 4 }}
                  isAnimationActive={false}
                />
              ))}
            </LineChart>
          </ResponsiveContainer>
        )}
      </div>

      <div className="bg-card border border-border rounded-nb-md p-4">
        <div className="flex items-baseline justify-between gap-4 mb-2 flex-wrap">
          <h4 className="m-0 text-sm font-bold tracking-tight">
            All event types
          </h4>
          <div className="flex items-center gap-3">
            <input
              type="text"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search…"
              aria-label="Search event types"
              className="border border-input rounded-md bg-background text-sm px-2.5 py-1.5 outline-none focus:ring-2 focus:ring-primary focus:border-primary"
            />
            <span className="font-mono text-[11px] text-muted-foreground whitespace-nowrap">
              {tableRows.length} of {series.length} · {periodLabel}
            </span>
          </div>
        </div>

        {tableRows.length === 0 ? (
          <EmptyState
            icon="—"
            title="No matching event types"
            description="Adjust the filter or search term."
          />
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-[12.5px] tabular-nums border-collapse">
              <thead>
                <tr className="bg-surface-2 text-muted-foreground">
                  <th className="text-left font-semibold uppercase tracking-[0.06em] text-[10.5px] px-3 py-2 whitespace-nowrap">
                    Event Type
                  </th>
                  <SortableHeader
                    label="Published"
                    active={sortCol === "published"}
                    desc={sortDesc}
                    onClick={() => onSort("published")}
                  />
                  <SortableHeader
                    label="Handled"
                    active={sortCol === "handled"}
                    desc={sortDesc}
                    onClick={() => onSort("handled")}
                  />
                  <SortableHeader
                    label="Failed"
                    active={sortCol === "failed"}
                    desc={sortDesc}
                    onClick={() => onSort("failed")}
                  />
                  <th className="text-right font-semibold uppercase tracking-[0.06em] text-[10.5px] px-3 py-2 whitespace-nowrap">
                    Share
                  </th>
                </tr>
              </thead>
              <tbody>
                {tableRows.map((r) => (
                  <tr key={r.eventTypeId} className="border-t border-border">
                    <td className="px-3 py-2 font-mono text-[11.5px] text-nimbus-purple font-semibold">
                      {r.eventTypeId}
                    </td>
                    <td className="px-3 py-2 text-right font-mono font-bold">
                      {r.published.toLocaleString()}
                    </td>
                    <td className="px-3 py-2 text-right font-mono">
                      {r.handled.toLocaleString()}
                    </td>
                    <td
                      className={cn(
                        "px-3 py-2 text-right font-mono",
                        r.failed > 0 && "text-status-danger font-bold",
                      )}
                    >
                      {r.failed.toLocaleString()}
                    </td>
                    <td className="px-3 py-2 text-right font-mono text-muted-foreground">
                      {formatShare(r.share)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}

function formatShare(share: number): string {
  const pct = share * 100;
  if (pct > 0 && pct < 0.1) return "<0.1%";
  return `${pct.toFixed(1)}%`;
}

const SortableHeader = ({
  label,
  active,
  desc,
  onClick,
}: {
  label: string;
  active: boolean;
  desc: boolean;
  onClick: () => void;
}) => (
  <th className="text-right font-semibold uppercase tracking-[0.06em] text-[10.5px] px-3 py-2 whitespace-nowrap">
    <button
      type="button"
      onClick={onClick}
      className={cn(
        "uppercase tracking-[0.06em] font-semibold hover:text-foreground",
        active && "text-foreground",
      )}
    >
      {label}
      <span aria-hidden="true" className="ml-1 inline-block w-2">
        {active ? (desc ? "↓" : "↑") : ""}
      </span>
    </button>
  </th>
);
