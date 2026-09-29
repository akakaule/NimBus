import * as api from "api-client";
import {
  FAILED_STATUSES,
  STATUS_COLORS,
} from "functions/failed-messages.functions";
import { cn } from "lib/utils";

const STATUS_HINTS: Record<string, string> = {
  Failed: "handler threw",
  DeadLettered: "max deliveries hit",
  Unsupported: "no handler",
};

// Matches status-info; endpoints are a count, not a status.
const ENDPOINTS_COLOR = "#3A6FB0";

export interface FailedLegendProps {
  totals: api.FailedHistogramTotals | undefined;
  /** The applied status filter; empty means all three. */
  status: string[];
  onToggleStatus: (status: string) => void;
  /** Hint under "Endpoints affected", e.g. the peak bucket. */
  endpointsHint: string;
  isLoading: boolean;
}

const count = (totals: api.FailedHistogramTotals | undefined, s: string) =>
  s === "Failed"
    ? totals?.failed
    : s === "DeadLettered"
      ? totals?.deadLettered
      : totals?.unsupported;

/**
 * Per-status totals under the chart, after Application Insights' legend tiles. Each status
 * tile also toggles that status in the filter.
 */
export default function FailedLegend({
  totals,
  status,
  onToggleStatus,
  endpointsHint,
  isLoading,
}: FailedLegendProps) {
  const value = (n: number | undefined) =>
    isLoading && !totals ? "—" : (n ?? 0).toLocaleString();

  return (
    <div
      className="flex flex-wrap items-stretch gap-x-9 gap-y-3 pt-1"
      aria-label="Totals"
    >
      {FAILED_STATUSES.map((s) => {
        const on = status.length === 0 || status.includes(s);
        return (
          <button
            key={s}
            type="button"
            aria-pressed={on}
            title={on ? `Hide ${s}` : `Show ${s}`}
            onClick={() => onToggleStatus(s)}
            className={cn(
              "flex items-stretch gap-2.5 rounded-nb-sm text-left transition-opacity",
              !on && "opacity-45",
            )}
          >
            <span
              className="w-[5px] rounded-[1px]"
              style={{ background: STATUS_COLORS[s] }}
            />
            <span className="flex flex-col">
              <span className="text-[12.5px] text-foreground">{s}</span>
              <span className="text-[28px] font-normal leading-tight text-foreground">
                {value(count(totals, s))}
              </span>
              <span className="font-mono text-[10.5px] text-muted-foreground">
                {STATUS_HINTS[s]}
              </span>
            </span>
          </button>
        );
      })}
      <span className="w-px bg-border" aria-hidden="true" />
      <div className="flex items-stretch gap-2.5">
        <span
          className="w-[5px] rounded-[1px]"
          style={{ background: ENDPOINTS_COLOR }}
        />
        <span className="flex flex-col">
          <span className="text-[12.5px] text-foreground">
            Endpoints affected
          </span>
          <span className="text-[28px] font-normal leading-tight text-foreground">
            {value(totals?.byEndpoint?.length)}
          </span>
          <span className="font-mono text-[10.5px] text-muted-foreground">
            {endpointsHint}
          </span>
        </span>
      </div>
    </div>
  );
}
