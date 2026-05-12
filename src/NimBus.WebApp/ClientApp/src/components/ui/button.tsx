import { forwardRef, type ButtonHTMLAttributes } from "react";
import { cn } from "lib/utils";

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: "solid" | "outline" | "ghost" | "link" | "quiet";
  size?: "xs" | "sm" | "md" | "lg";
  colorScheme?: "primary" | "gray" | "red" | "green" | "blue";
  isLoading?: boolean;
  leftIcon?: React.ReactNode;
  rightIcon?: React.ReactNode;
}

const Button = forwardRef<HTMLButtonElement, ButtonProps>(
  (
    {
      className,
      variant = "solid",
      size = "md",
      colorScheme = "primary",
      isLoading = false,
      leftIcon,
      rightIcon,
      disabled,
      children,
      ...props
    },
    ref,
  ) => {
    const baseStyles =
      "inline-flex items-center justify-center font-semibold rounded-nb-md transition-colors focus:outline-none focus:ring-2 focus:ring-offset-1 focus:ring-offset-background disabled:opacity-50 disabled:cursor-not-allowed";

    const variants = {
      // Solid — primary coral CTA (NimBus). Other color schemes preserved
      // for legacy callsites; semantically use `red` for danger CTAs.
      solid: {
        primary:
          "bg-primary text-white border border-primary hover:bg-primary-600 hover:border-primary-600 focus:ring-primary",
        gray: "bg-ink text-canvas border border-ink hover:bg-ink-2 focus:ring-ink-2 dark:bg-zinc-700 dark:hover:bg-zinc-600 dark:text-foreground",
        red: "bg-status-danger text-white border border-status-danger hover:opacity-90 focus:ring-status-danger",
        green:
          "bg-status-success text-white border border-status-success hover:opacity-90 focus:ring-status-success",
        blue: "bg-status-info text-white border border-status-info hover:opacity-90 focus:ring-status-info",
      },
      // Outline — primary outline matches the design's btn-outline (coral border, surface bg, coral-600 text).
      outline: {
        primary:
          "bg-card text-primary-600 border border-primary hover:bg-primary-tint focus:ring-primary dark:text-primary-400 dark:bg-card dark:hover:bg-primary-900/40",
        gray: "bg-card text-foreground border border-border-strong hover:bg-muted focus:ring-border-strong",
        red: "bg-transparent text-status-danger border border-status-danger hover:bg-status-danger-50 focus:ring-status-danger dark:hover:bg-red-950/40",
        green:
          "bg-transparent text-status-success border border-status-success hover:bg-status-success-50 focus:ring-status-success dark:hover:bg-green-950/40",
        blue: "bg-transparent text-status-info border border-status-info hover:bg-status-info-50 focus:ring-status-info dark:hover:bg-blue-950/40",
      },
      // Ghost — borderless. Primary uses ink (text) on hover surface-2, per design.
      ghost: {
        primary:
          "text-foreground border border-border-strong bg-transparent hover:bg-muted focus:ring-border-strong",
        gray: "text-muted-foreground border border-transparent hover:bg-muted hover:text-foreground focus:ring-border-strong",
        red: "text-status-danger border border-transparent hover:bg-status-danger-50 focus:ring-status-danger dark:hover:bg-red-950/40",
        green:
          "text-status-success border border-transparent hover:bg-status-success-50 focus:ring-status-success dark:hover:bg-green-950/40",
        blue: "text-status-info border border-transparent hover:bg-status-info-50 focus:ring-status-info dark:hover:bg-blue-950/40",
      },
      // Quiet — soft filled action (Export CSV, Save view). Surface-2 fill, no border.
      quiet: {
        primary:
          "bg-muted text-foreground border border-transparent hover:bg-[#DDD7C6] dark:hover:bg-[#3D3830] focus:ring-border-strong",
        gray: "bg-muted text-muted-foreground border border-transparent hover:bg-[#DDD7C6] dark:hover:bg-[#3D3830] focus:ring-border-strong",
        red: "bg-status-danger-50 text-status-danger-ink border border-transparent hover:bg-status-danger-50/80 focus:ring-status-danger",
        green:
          "bg-status-success-50 text-status-success-ink border border-transparent hover:bg-status-success-50/80 focus:ring-status-success",
        blue: "bg-status-info-50 text-status-info-ink border border-transparent hover:bg-status-info-50/80 focus:ring-status-info",
      },
      link: {
        primary:
          "text-primary underline-offset-4 hover:underline dark:text-primary-400",
        gray: "text-muted-foreground underline-offset-4 hover:underline",
        red: "text-status-danger underline-offset-4 hover:underline",
        green: "text-status-success underline-offset-4 hover:underline",
        blue: "text-status-info underline-offset-4 hover:underline",
      },
    };

    const sizes = {
      xs: "h-6 px-2 text-xs gap-1",
      sm: "h-8 px-3 text-xs gap-1.5",
      md: "h-9 px-4 text-sm gap-2",
      lg: "h-11 px-5 text-[15px] gap-2",
    };

    return (
      <button
        ref={ref}
        className={cn(
          baseStyles,
          variants[variant][colorScheme],
          sizes[size],
          className,
        )}
        disabled={disabled || isLoading}
        {...props}
      >
        {isLoading ? (
          <svg
            className="animate-spin -ml-1 mr-2 h-4 w-4"
            xmlns="http://www.w3.org/2000/svg"
            fill="none"
            viewBox="0 0 24 24"
          >
            <circle
              className="opacity-25"
              cx="12"
              cy="12"
              r="10"
              stroke="currentColor"
              strokeWidth="4"
            />
            <path
              className="opacity-75"
              fill="currentColor"
              d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"
            />
          </svg>
        ) : leftIcon ? (
          <span className="inline-flex shrink-0">{leftIcon}</span>
        ) : null}
        {children}
        {rightIcon && !isLoading ? (
          <span className="inline-flex shrink-0">{rightIcon}</span>
        ) : null}
      </button>
    );
  },
);

Button.displayName = "Button";

export { Button };
