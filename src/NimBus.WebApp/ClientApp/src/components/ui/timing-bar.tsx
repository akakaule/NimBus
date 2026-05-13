import { cn } from "lib/utils";
import { type ReactNode } from "react";

export interface TimingSegment {
  /** Label rendered in the legend, e.g. "Queue". */
  label: string;
  /** Display value (string already formatted, e.g. "3.32 s"). */
  display: string;
  /** Raw numeric weight used for proportional segment widths. */
  weight: number;
  /** Tailwind-style colour class for the bar segment + legend key. */
  colorClass: string;
}

export interface TimingBarProps {
  segments: TimingSegment[];
  /** Total label on the right (e.g. "3.32 s · Completed"). */
  total?: ReactNode;
  /** Optional trailing note rendered italic-muted under the legend. */
  trailing?: ReactNode;
  className?: string;
}

/**
 * Single-line stacked horizontal timing bar (queue · processing · ack).
 *
 * Design rec §09 event-details: "A 1-line stacked bar tells the same story
 * in 80 fewer pixels and immediately flags slow events." Each segment width
 * scales with its weight; zero-weight segments still render at a small min
 * width so the legend stays grounded.
 */
export const TimingBar: React.FC<TimingBarProps> = ({
  segments,
  total,
  trailing,
  className,
}) => {
  const totalWeight = segments.reduce((s, x) => s + Math.max(x.weight, 0), 0);
  const hasWeight = totalWeight > 0;

  return (
    <div
      className={cn(
        "bg-card border border-border rounded-nb-md px-4 py-3.5",
        className,
      )}
    >
      <div className="flex items-baseline justify-between mb-2">
        <span className="font-mono text-[10.5px] uppercase tracking-[0.12em] text-muted-foreground">
          Timing
        </span>
        {total && (
          <span className="font-mono text-[13px] font-bold">{total}</span>
        )}
      </div>
      <div
        className="flex h-2.5 rounded-full overflow-hidden bg-muted"
        aria-label="Timing breakdown"
      >
        {segments.map((seg) => (
          <span
            key={seg.label}
            title={`${seg.label}: ${seg.display}`}
            className={cn(seg.colorClass, "h-full")}
            style={{
              flex: hasWeight
                ? `${Math.max(seg.weight, 0.01)} 0 auto`
                : "1 0 auto",
              minWidth: "2px",
            }}
          />
        ))}
      </div>
      <div className="flex gap-4 flex-wrap mt-2.5 items-baseline font-mono text-[11px] text-muted-foreground">
        {segments.map((seg) => (
          <span key={seg.label} className="inline-flex items-center gap-1.5">
            <span
              aria-hidden="true"
              className={cn("inline-block w-2.5 h-2.5 rounded-[2px]", seg.colorClass)}
            />
            <span>
              {seg.label} {seg.display}
            </span>
          </span>
        ))}
        {trailing && (
          <span className="ml-auto italic text-muted-foreground">
            {trailing}
          </span>
        )}
      </div>
    </div>
  );
};
