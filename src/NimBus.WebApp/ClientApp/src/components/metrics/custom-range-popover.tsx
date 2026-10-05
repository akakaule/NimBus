import * as React from "react";
import moment from "moment";
import CustomRangeForm from "components/common/custom-range-form";
import type { TimeRange } from "functions/time-range.functions";

/** A custom range as compact local text, e.g. "20/09 08:00–21/09 17:30". */
export function formatRange(range: TimeRange): string {
  const fmt = (d: Date) => moment(d).format("DD/MM HH:mm");
  return `${fmt(range.from)}–${fmt(range.to)}`;
}

export interface CustomRangePopoverProps {
  /** The applied custom range, if any; it highlights the trigger. */
  range?: TimeRange;
  /** The range on screen, to start the fields from when no custom range is applied. */
  shown: TimeRange;
  onApply: (range: TimeRange) => void;
  /** Classes for the trigger, so it matches the segmented control it sits in. */
  className?: string;
}

/**
 * The Custom… segment of a period switcher: opens local start and end fields, applied
 * together with Apply, after Application Insights' time range picker.
 */
export default function CustomRangePopover({
  range,
  shown,
  onApply,
  className,
}: CustomRangePopoverProps) {
  const [open, setOpen] = React.useState(false);
  const ref = React.useRef<HTMLSpanElement>(null);
  const trigger = React.useRef<HTMLButtonElement>(null);
  const close = React.useCallback(() => {
    setOpen(false);
    trigger.current?.focus();
  }, []);

  React.useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (!ref.current?.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") close();
    };
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open, close]);

  return (
    <span ref={ref} className="relative inline-flex">
      <button
        ref={trigger}
        type="button"
        aria-haspopup="dialog"
        aria-expanded={open}
        aria-pressed={!!range}
        onClick={() => setOpen((o) => !o)}
        className={className}
      >
        {range ? formatRange(range) : "Custom…"}
      </button>
      {open && (
        <div
          role="dialog"
          aria-label="Custom range"
          className="absolute right-0 top-full z-40 mt-1.5 rounded-nb-md border border-border-strong bg-card p-3 text-sm shadow-nb-lg animate-fade-in"
        >
          <CustomRangeForm
            initial={range ?? shown}
            onApply={(next) => {
              onApply(next);
              close();
            }}
            onCancel={close}
            className="w-[280px]"
          />
        </div>
      )}
    </span>
  );
}
