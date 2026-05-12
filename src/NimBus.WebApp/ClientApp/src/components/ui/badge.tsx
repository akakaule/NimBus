import { type HTMLAttributes } from "react";
import { cn } from "lib/utils";

export type BadgeVariant =
  | "default"
  // NimBus message-status variants
  | "failed"
  | "pending"
  | "completed"
  | "deferred"
  | "deadlettered"
  | "skipped"
  | "unsupported"
  // Semantic variants
  | "primary"
  | "secondary"
  | "success"
  | "warning"
  | "error"
  | "info";

export interface BadgeProps extends HTMLAttributes<HTMLSpanElement> {
  variant?: BadgeVariant;
  size?: "sm" | "md" | "lg";
  /**
   * Show a status dot before the label. Defaults to true for status variants
   * (failed/pending/completed/etc.) and false for primary/secondary.
   */
  withDot?: boolean;
}

const STATUS_VARIANTS = new Set<BadgeVariant>([
  "failed",
  "pending",
  "completed",
  "deferred",
  "deadlettered",
  "skipped",
  "unsupported",
  "success",
  "warning",
  "error",
  "info",
]);

const Badge = ({
  className,
  variant = "default",
  size = "md",
  withDot,
  children,
  ...props
}: BadgeProps) => {
  // Tints + dot colors. One semantic per hue per design system §02.
  const variants: Record<BadgeVariant, { pill: string; dot: string }> = {
    default: {
      pill: "bg-muted text-foreground",
      dot: "bg-ink-3",
    },
    // Message statuses
    failed: {
      pill: "bg-status-danger-50 text-status-danger-ink dark:bg-red-950/40 dark:text-red-200",
      dot: "bg-status-danger",
    },
    pending: {
      pill: "bg-status-warning-50 text-status-warning-ink dark:bg-yellow-950/40 dark:text-yellow-200",
      dot: "bg-status-warning",
    },
    completed: {
      pill: "bg-status-success-50 text-status-success-ink dark:bg-green-950/40 dark:text-green-200",
      dot: "bg-status-success",
    },
    deferred: {
      pill: "bg-status-warning-50 text-status-warning-ink dark:bg-yellow-950/40 dark:text-yellow-200",
      dot: "bg-status-warning",
    },
    deadlettered: {
      pill: "bg-status-danger-50 text-status-danger-ink dark:bg-red-950/40 dark:text-red-200",
      dot: "bg-status-danger",
    },
    skipped: {
      pill: "bg-muted text-muted-foreground",
      dot: "bg-ink-3",
    },
    unsupported: {
      pill: "bg-muted text-muted-foreground",
      dot: "bg-ink-3",
    },
    // Semantic
    primary: {
      pill: "bg-primary text-white",
      dot: "bg-white/80",
    },
    secondary: {
      pill: "bg-muted text-muted-foreground",
      dot: "bg-ink-3",
    },
    success: {
      pill: "bg-status-success-50 text-status-success-ink dark:bg-green-950/40 dark:text-green-200",
      dot: "bg-status-success",
    },
    warning: {
      pill: "bg-status-warning-50 text-status-warning-ink dark:bg-yellow-950/40 dark:text-yellow-200",
      dot: "bg-status-warning",
    },
    error: {
      pill: "bg-status-danger-50 text-status-danger-ink dark:bg-red-950/40 dark:text-red-200",
      dot: "bg-status-danger",
    },
    info: {
      pill: "bg-status-info-50 text-status-info-ink dark:bg-blue-950/40 dark:text-blue-200",
      dot: "bg-status-info",
    },
  };

  const sizes = {
    sm: "px-2 py-0.5 text-[10px] gap-1",
    md: "px-2 py-[3px] text-xs gap-1.5",
    lg: "px-2.5 py-1 text-sm gap-2",
  };

  const dotSizes = {
    sm: "w-1.5 h-1.5",
    md: "w-1.5 h-1.5",
    lg: "w-2 h-2",
  };

  const { pill, dot } = variants[variant];
  const showDot = withDot ?? STATUS_VARIANTS.has(variant);

  return (
    <span
      className={cn(
        "inline-flex items-center font-semibold rounded-full whitespace-nowrap",
        pill,
        sizes[size],
        className,
      )}
      {...props}
    >
      {showDot && (
        <span
          aria-hidden="true"
          className={cn("rounded-full inline-block", dotSizes[size], dot)}
        />
      )}
      {children}
    </span>
  );
};

export { Badge };
