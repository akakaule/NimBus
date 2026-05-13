import { type ReactNode } from "react";
import { cn } from "lib/utils";

export interface FilterChipProps {
  /** Field label shown before the colon, e.g. "Status". */
  field?: string;
  /** Active value, e.g. "Failed, Deferred". */
  value: ReactNode;
  /** Called when the × is clicked. Omit to render a non-removable chip. */
  onRemove?: () => void;
  className?: string;
}

/**
 * Removable filter chip — coral on cream by default, mono font.
 * Used inside `FilterToolbar` so the active filter state is the single
 * source of truth (design rec §04: collapse the two filter zones into one).
 */
export const FilterChip = ({
  field,
  value,
  onRemove,
  className,
}: FilterChipProps) => (
  <span
    className={cn(
      "inline-flex items-center gap-1.5 font-mono text-[11.5px] font-semibold",
      "bg-primary-tint text-primary-600 rounded-nb-sm pl-2.5 pr-2 py-1",
      "dark:bg-primary/20 dark:text-primary-400",
      className,
    )}
  >
    {field && <span className="opacity-70">{field}:</span>}
    <span>{value}</span>
    {onRemove && (
      <button
        type="button"
        onClick={onRemove}
        className="text-current text-[13px] leading-none pl-0.5 hover:opacity-70"
        aria-label="Remove filter"
      >
        ×
      </button>
    )}
  </span>
);

export interface FilterToolbarProps {
  /** Search input on the left. */
  search?: ReactNode;
  /** Active filter chips. */
  chips?: ReactNode;
  /** "+ Add filter" / "Save view" buttons on the right of chips. */
  actions?: ReactNode;
  /** Right-aligned trailing slot (e.g. view-mode segmented control). */
  trailing?: ReactNode;
  className?: string;
}

/**
 * Single inline filter row — surface card with search, chips, and trailing
 * actions. Replaces the legacy three-zone filter layout (inputs row +
 * "Advanced filters" drawer + in-table search) — design rec §04.
 */
export const FilterToolbar = ({
  search,
  chips,
  actions,
  trailing,
  className,
}: FilterToolbarProps) => (
  <div
    className={cn(
      "flex items-center gap-2 flex-wrap",
      "bg-card border border-border rounded-nb-md p-2.5",
      className,
    )}
  >
    {search && <div className="flex-1 min-w-[240px]">{search}</div>}
    {chips}
    {actions}
    {trailing && <div className="ml-auto">{trailing}</div>}
  </div>
);

export interface FilterSearchProps {
  value: string;
  onChange: (next: string) => void;
  placeholder?: string;
  className?: string;
}

/**
 * Search-input variant tuned for the toolbar — magnifier on the left,
 * cream surface, no border because the toolbar already has one.
 */
export const FilterSearch = ({
  value,
  onChange,
  placeholder = "Filter…",
  className,
}: FilterSearchProps) => (
  <div className={cn("relative", className)}>
    <span
      aria-hidden="true"
      className="absolute left-3 top-1/2 -translate-y-1/2 text-muted-foreground"
    >
      <svg width="14" height="14" viewBox="0 0 16 16" fill="none">
        <circle cx="7" cy="7" r="4.5" stroke="currentColor" strokeWidth="1.5" />
        <path
          d="M10.5 10.5L13 13"
          stroke="currentColor"
          strokeWidth="1.5"
          strokeLinecap="round"
        />
      </svg>
    </span>
    <input
      type="text"
      value={value}
      onChange={(e) => onChange(e.target.value)}
      placeholder={placeholder}
      className={cn(
        "w-full text-[13px] text-foreground bg-background dark:bg-muted",
        "border border-border rounded-nb-md pl-9 pr-3 py-2",
        "placeholder:text-muted-foreground",
        "focus:outline-none focus:border-primary focus:ring-2 focus:ring-primary/30",
      )}
    />
  </div>
);
