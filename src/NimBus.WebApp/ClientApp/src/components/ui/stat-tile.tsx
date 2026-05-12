import { type ReactNode } from "react";
import { cn } from "lib/utils";

export type StatTileTone = "default" | "warning" | "danger" | "muted";

export interface StatTileProps {
  /** Caps-mono micro label above the value. */
  label: string;
  /** Hero number — rendered with tabular figures. */
  value: ReactNode;
  /** Optional unit suffix (e.g. "ms") shown small after the value. */
  unit?: string;
  /** Single-line delta below the value (e.g. "▲ 3.4% vs prior 1h"). */
  delta?: ReactNode;
  /** Tone tints the delta color and signals "needs attention" via context. */
  tone?: StatTileTone;
  className?: string;
  onClick?: () => void;
}

const deltaTone: Record<StatTileTone, string> = {
  default: "text-status-success",
  warning: "text-status-warning",
  danger: "text-status-danger",
  muted: "text-muted-foreground",
};

/**
 * Stat tile from the design system §06 — promotes summary metrics above
 * the table so the page answers "how is this endpoint doing?" before the
 * operator scans rows.
 */
export const StatTile = ({
  label,
  value,
  unit,
  delta,
  tone = "default",
  className,
  onClick,
}: StatTileProps) => {
  const Comp = onClick ? "button" : "div";
  return (
    <Comp
      onClick={onClick}
      className={cn(
        "text-left bg-card border border-border rounded-nb-md px-4 py-3.5",
        "transition-colors",
        onClick && "hover:border-border-strong cursor-pointer",
        className,
      )}
    >
      <div className="font-mono text-[10px] tracking-[0.12em] uppercase text-muted-foreground mb-1.5">
        {label}
      </div>
      <div className="text-[28px] font-bold leading-none tracking-tight tabular-nums">
        {value}
        {unit && (
          <span className="text-[13px] font-semibold text-muted-foreground ml-0.5">
            {unit}
          </span>
        )}
      </div>
      {delta && (
        <div className={cn("mt-2 font-mono text-[11px]", deltaTone[tone])}>
          {delta}
        </div>
      )}
    </Comp>
  );
};

export interface StatRowProps {
  children: ReactNode;
  className?: string;
  /** Default `4` matches design's `.stats { grid-template-columns: repeat(4,1fr) }`. */
  columns?: 2 | 3 | 4 | 5;
}

const colsClass: Record<number, string> = {
  2: "grid-cols-2",
  3: "grid-cols-3",
  4: "grid-cols-4",
  5: "grid-cols-5",
};

export const StatRow = ({ children, className, columns = 4 }: StatRowProps) => (
  <div className={cn("grid gap-3", colsClass[columns], className)}>
    {children}
  </div>
);
