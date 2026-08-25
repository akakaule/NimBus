import { forwardRef } from "react";
import { cn } from "lib/utils";

export interface ToggleProps {
  checked: boolean;
  onChange: (next: boolean) => void;
  /** When set, renders a short uppercase On/Off label beside the track. */
  showStateLabel?: boolean;
  disabled?: boolean;
  className?: string;
  "aria-label": string;
}

// Accessible on/off switch (role="switch"). Calls preventDefault +
// stopPropagation on activation so it is safe inside a clickable table row —
// the data-table's row handler skips clicks that land on a <button>, so
// flipping the switch never triggers row navigation.
const Toggle = forwardRef<HTMLButtonElement, ToggleProps>(
  (
    {
      checked,
      onChange,
      showStateLabel = true,
      disabled = false,
      className,
      ...props
    },
    ref,
  ) => {
    return (
      <button
        ref={ref}
        type="button"
        role="switch"
        aria-checked={checked}
        disabled={disabled}
        onClick={(e) => {
          e.preventDefault();
          e.stopPropagation();
          if (!disabled) onChange(!checked);
        }}
        className={cn(
          "inline-flex items-center gap-2 align-middle select-none",
          "focus:outline-none focus-visible:ring-2 focus-visible:ring-primary-tint rounded-full",
          disabled ? "cursor-not-allowed opacity-60" : "cursor-pointer",
          className,
        )}
        {...props}
      >
        <span
          className={cn(
            "relative h-5 w-[34px] rounded-full transition-colors",
            "shadow-[inset_0_1px_2px_rgba(0,0,0,0.06)]",
            checked ? "bg-status-success" : "bg-border-strong",
          )}
        >
          <span
            className={cn(
              "absolute top-0.5 h-4 w-4 rounded-full bg-white shadow-[0_1px_2px_rgba(0,0,0,0.2)] transition-[left]",
              checked ? "left-[16px]" : "left-0.5",
            )}
          />
        </span>
        {showStateLabel && (
          <span
            className={cn(
              "inline-block w-6 font-mono text-[10.5px] font-semibold uppercase tracking-wide",
              checked ? "text-status-success" : "text-muted-foreground",
            )}
          >
            {checked ? "On" : "Off"}
          </span>
        )}
      </button>
    );
  },
);

Toggle.displayName = "Toggle";

export { Toggle };
