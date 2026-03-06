import { type HTMLAttributes } from "react";
import { cn } from "lib/utils";

export type BadgeVariant =
  | "default"
  | "failed"
  | "pending"
  | "completed"
  | "deferred"
  | "deadlettered"
  | "skipped"
  | "unsupported"
  | "primary"
  | "secondary"
  | "success"
  | "warning"
  | "error"
  | "info";

export interface BadgeProps extends HTMLAttributes<HTMLSpanElement> {
  variant?: BadgeVariant;
  size?: "sm" | "md" | "lg";
}

const Badge = ({
  className,
  variant = "default",
  size = "md",
  children,
  ...props
}: BadgeProps) => {
  const variants: Record<BadgeVariant, string> = {
    default: "bg-muted text-foreground",
    // Status variants (matching status colors from tailwind config)
    failed: "bg-status-failed text-white",
    pending: "bg-status-pending text-gray-900",
    completed: "bg-status-completed text-white",
    deferred: "bg-status-deferred text-white",
    deadlettered: "bg-status-deadlettered text-white",
    skipped: "bg-status-skipped text-white",
    unsupported: "bg-status-unsupported text-white",
    // Semantic variants
    primary: "bg-primary text-white",
    secondary: "bg-gray-500 text-white",
    success: "bg-green-500 text-white",
    warning: "bg-yellow-500 text-gray-900",
    error: "bg-red-500 text-white",
    info: "bg-blue-500 text-white",
  };

  const sizes = {
    sm: "px-1.5 py-0.5 text-xs",
    md: "px-2 py-0.5 text-xs",
    lg: "px-2.5 py-1 text-sm",
  };

  return (
    <span
      className={cn(
        "inline-flex items-center font-medium rounded-full",
        variants[variant],
        sizes[size],
        className,
      )}
      {...props}
    >
      {children}
    </span>
  );
};

export { Badge };
