import { forwardRef, type ButtonHTMLAttributes } from "react";
import { cn } from "lib/utils";

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: "solid" | "outline" | "ghost" | "link";
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
      "inline-flex items-center justify-center font-medium rounded-md transition-colors focus:outline-none focus:ring-2 focus:ring-offset-2 disabled:opacity-50 disabled:cursor-not-allowed";

    const variants = {
      solid: {
        primary:
          "bg-primary text-white hover:bg-primary-600 focus:ring-primary-500",
        gray: "bg-gray-600 text-white hover:bg-gray-700 focus:ring-gray-500 dark:bg-zinc-700 dark:hover:bg-zinc-600",
        red: "bg-red-600 text-white hover:bg-red-700 focus:ring-red-500 dark:bg-red-700 dark:hover:bg-red-600",
        green:
          "bg-green-600 text-white hover:bg-green-700 focus:ring-green-500 dark:bg-green-700 dark:hover:bg-green-600",
        blue: "bg-blue-600 text-white hover:bg-blue-700 focus:ring-blue-500 dark:bg-blue-700 dark:hover:bg-blue-600",
      },
      outline: {
        primary:
          "border-2 border-primary text-primary hover:bg-primary hover:text-white focus:ring-primary-500 dark:text-primary-400 dark:border-primary-500 dark:hover:bg-primary-900/40 dark:hover:text-primary-200",
        gray: "border-2 border-input text-foreground hover:bg-accent focus:ring-gray-500",
        red: "border-2 border-red-500 text-red-500 hover:bg-red-50 focus:ring-red-500 dark:text-red-400 dark:border-red-600 dark:hover:bg-red-950/40",
        green:
          "border-2 border-green-500 text-green-500 hover:bg-green-50 focus:ring-green-500 dark:text-green-400 dark:border-green-600 dark:hover:bg-green-950/40",
        blue: "border-2 border-blue-500 text-blue-500 hover:bg-blue-50 focus:ring-blue-500 dark:text-blue-400 dark:border-blue-600 dark:hover:bg-blue-950/40",
      },
      ghost: {
        primary: "text-primary hover:bg-primary-50 focus:ring-primary-500 dark:text-primary-400 dark:hover:bg-primary-900/40",
        gray: "text-muted-foreground hover:bg-accent hover:text-accent-foreground focus:ring-gray-500",
        red: "text-red-500 hover:bg-red-50 focus:ring-red-500 dark:text-red-400 dark:hover:bg-red-950/40",
        green: "text-green-500 hover:bg-green-50 focus:ring-green-500 dark:text-green-400 dark:hover:bg-green-950/40",
        blue: "text-blue-500 hover:bg-blue-50 focus:ring-blue-500 dark:text-blue-400 dark:hover:bg-blue-950/40",
      },
      link: {
        primary: "text-primary underline-offset-4 hover:underline dark:text-primary-400",
        gray: "text-muted-foreground underline-offset-4 hover:underline",
        red: "text-red-500 underline-offset-4 hover:underline dark:text-red-400",
        green: "text-green-500 underline-offset-4 hover:underline dark:text-green-400",
        blue: "text-blue-500 underline-offset-4 hover:underline dark:text-blue-400",
      },
    };

    const sizes = {
      xs: "h-6 px-2 text-xs gap-1",
      sm: "h-8 px-3 text-sm gap-1.5",
      md: "h-10 px-4 text-sm gap-2",
      lg: "h-12 px-6 text-base gap-2",
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
