import {
  FLOW_COLORS,
  FLOW_LEGEND,
  axisTicks,
  formatSpan,
  type FlowRow,
} from "functions/flow-gantt.functions";
import { cn } from "lib/utils";
import moment from "moment";
import { Link } from "react-router-dom";

export interface FlowGanttProps {
  rows: FlowRow[];
  spanMs: number;
  /** Rows left out beyond the cap. */
  hidden: number;
  selectedKey?: string;
  onSelect: (row: FlowRow) => void;
  /** Where the complete history lives (Event Details). */
  fullHistoryHref: string;
}

const pct = (value: number, span: number) =>
  span > 0 ? Math.min((value / span) * 100, 100) : 0;

/**
 * The event's messages and audits against one time axis, after Application Insights'
 * end-to-end transaction view. Requests are spans up to their response; the rest are points.
 */
export default function FlowGantt({
  rows,
  spanMs,
  hidden,
  selectedKey,
  onSelect,
  fullHistoryHref,
}: FlowGanttProps) {
  const ticks = axisTicks(spanMs);
  // Leave room after the last point so its marker is not clipped.
  const axis = Math.max(spanMs, ticks[ticks.length - 1] ?? 0) * 1.02 || 1;

  return (
    <section
      aria-label="End-to-end message flow"
      className="rounded-nb-md border border-border bg-background"
    >
      <div className="flex flex-wrap items-center justify-between gap-2 border-b border-border px-3.5 py-2.5">
        <h3 className="m-0 text-[14.5px] font-bold">End-to-end message flow</h3>
        <div className="flex flex-wrap gap-3.5 text-[11.5px] text-muted-foreground">
          {FLOW_LEGEND.map((l) => (
            <span key={l.kind} className="inline-flex items-center gap-1.5">
              <span
                className="inline-block h-2.5 w-2.5 rounded-[2px]"
                style={{ background: FLOW_COLORS[l.kind] }}
              />
              {l.label}
            </span>
          ))}
        </div>
      </div>
      {rows.length === 0 ? (
        <p className="m-0 px-3.5 py-6 text-center text-sm text-muted-foreground">
          No message history available for this event.
        </p>
      ) : (
        <>
          <div className="flex border-b border-border px-3.5 pb-1 pt-1.5 text-[11px] text-muted-foreground">
            <div className="w-[300px] shrink-0 font-bold text-foreground">
              Message
            </div>
            <div className="relative h-3.5 flex-1" aria-hidden="true">
              {ticks.map((t) => (
                <span
                  key={t}
                  className="absolute -translate-x-1/2 first:translate-x-0"
                  style={{ left: `${pct(t, axis)}%` }}
                >
                  {formatSpan(t)}
                </span>
              ))}
            </div>
            <div className="w-[76px] shrink-0 text-right font-bold text-foreground">
              Duration
            </div>
          </div>
          <ol className="m-0 list-none p-0" aria-label="Messages">
            {rows.map((row) => {
              const selected = row.key === selectedKey;
              const color = FLOW_COLORS[row.kind];
              const left = pct(row.offsetMs, axis);
              return (
                <li key={row.key}>
                  <button
                    type="button"
                    aria-pressed={selected}
                    onClick={() => onSelect(row)}
                    className={cn(
                      "flex w-full items-center border-b border-border/60 px-3.5 py-1.5 text-left hover:bg-muted",
                      selected &&
                        "bg-primary-50 shadow-[inset_3px_0_0_#E8743C] dark:bg-primary-900/30",
                    )}
                  >
                    <span className="flex w-[300px] min-w-0 shrink-0 flex-col">
                      <span className="whitespace-nowrap text-[13px] font-bold text-foreground">
                        {row.label}
                      </span>
                      <span className="truncate text-[11.5px] text-muted-foreground">
                        <span className="font-mono">
                          {moment(row.at).format("HH:mm:ss.SSS")}
                        </span>
                        {row.route && ` · ${row.route}`}
                      </span>
                    </span>
                    <span className="relative h-[26px] flex-1" aria-hidden="true">
                      <span className="absolute inset-x-0 top-1/2 border-t border-dashed border-border" />
                      {row.durationMs !== undefined ? (
                        <span
                          className="absolute top-[7px] h-3 min-w-[6px] rounded-[2px]"
                          style={{
                            left: `${left}%`,
                            width: `${pct(row.durationMs, axis)}%`,
                            background: color,
                          }}
                        />
                      ) : (
                        <span
                          className="absolute top-2 ml-[-5px] h-2.5 w-2.5 rotate-45"
                          style={{ left: `${left}%`, background: color }}
                        />
                      )}
                    </span>
                    <span className="w-[76px] shrink-0 text-right font-mono text-[12px] text-foreground">
                      {row.durationMs !== undefined
                        ? formatSpan(row.durationMs)
                        : "—"}
                    </span>
                  </button>
                </li>
              );
            })}
          </ol>
          {hidden > 0 && (
            <p className="m-0 px-3.5 py-2 text-[12.5px] text-muted-foreground">
              {hidden} later entr{hidden === 1 ? "y is" : "ies are"} not shown.{" "}
              <Link
                to={fullHistoryHref}
                className="font-semibold text-primary-700 hover:underline"
              >
                Show all in Event Details
              </Link>
            </p>
          )}
        </>
      )}
    </section>
  );
}
