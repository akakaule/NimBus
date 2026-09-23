import { cn } from "lib/utils";
import { SPEEDS } from "./simulation-utils";

/** The six speed multipliers the server accepts. */
export default function SpeedControl({
  speed,
  disabled,
  onChange,
}: {
  speed: number;
  disabled?: boolean;
  onChange: (speed: number) => void;
}) {
  return (
    <div role="radiogroup" aria-label="Speed" className="inline-flex overflow-hidden rounded-md border border-border">
      {SPEEDS.map((value) => (
        <button
          key={value}
          type="button"
          role="radio"
          aria-checked={speed === value}
          disabled={disabled}
          onClick={() => speed !== value && onChange(value)}
          className={cn(
            "px-3 py-1.5 font-mono text-xs disabled:opacity-50",
            speed === value ? "bg-primary text-primary-foreground" : "bg-card hover:bg-muted",
          )}
        >
          {value}×
        </button>
      ))}
    </div>
  );
}
