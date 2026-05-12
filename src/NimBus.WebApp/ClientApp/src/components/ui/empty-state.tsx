import { type ReactNode } from "react";
import { cn } from "lib/utils";

export interface EmptyStateProps {
  /** Optional icon — pass a single SVG/character node. */
  icon?: ReactNode;
  /** Bold one-line headline. */
  title: ReactNode;
  /** Optional supporting copy under the title. */
  description?: ReactNode;
  /** Primary action button(s) under the description. */
  action?: ReactNode;
  /** Tone tints the icon background (default: muted; success: green). */
  tone?: "muted" | "success" | "warning" | "danger" | "info";
  className?: string;
}

const iconBgTone = {
  muted: "bg-muted text-muted-foreground",
  success: "bg-status-success-50 text-status-success",
  warning: "bg-status-warning-50 text-status-warning",
  danger: "bg-status-danger-50 text-status-danger",
  info: "bg-status-info-50 text-status-info",
};

/**
 * Empty / zero-row state. Used wherever filtered results return nothing,
 * or a metric panel has "good news" to show (e.g. "no failures") — design
 * recs §08 and §09 metrics. Empty states are a teaching moment, not an error.
 */
export const EmptyState = ({
  icon,
  title,
  description,
  action,
  tone = "muted",
  className,
}: EmptyStateProps) => (
  <div
    className={cn(
      "flex flex-col items-center justify-center text-center gap-3",
      "py-12 px-6 rounded-nb-md bg-card border border-border",
      className,
    )}
  >
    {icon && (
      <div
        className={cn(
          "w-12 h-12 rounded-full inline-flex items-center justify-center text-xl font-bold",
          iconBgTone[tone],
        )}
      >
        {icon}
      </div>
    )}
    <div className="font-bold text-base text-foreground">{title}</div>
    {description && (
      <p className="text-[13px] text-muted-foreground max-w-md leading-relaxed m-0">
        {description}
      </p>
    )}
    {action && <div className="mt-1">{action}</div>}
  </div>
);
