import * as React from "react";
import moment from "moment";
import { Button } from "components/ui/button";
import { Input } from "components/ui/input";

/** Longest custom range. Mirrors MetricsImplementation.MaxWindow on the server. */
export const MAX_CUSTOM_RANGE_DAYS = 90;

export interface TimeRange {
  from: Date;
  to: Date;
}

// The value format of a datetime-local input, read and written in local time.
const LOCAL_INPUT = "YYYY-MM-DDTHH:mm";

/** Why the server would refuse a custom range, or undefined when it is valid. */
export function customRangeError(from: Date, to: Date): string | undefined {
  if (Number.isNaN(from.getTime()) || Number.isNaN(to.getTime()))
    return "Enter a start and an end time.";
  if (from >= to) return "The start must be before the end.";
  if (to.getTime() - from.getTime() > MAX_CUSTOM_RANGE_DAYS * 86_400_000)
    return `A custom range may span at most ${MAX_CUSTOM_RANGE_DAYS} days.`;
  return undefined;
}

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
          <RangeForm
            initial={range ?? shown}
            onApply={(next) => {
              onApply(next);
              close();
            }}
            onCancel={close}
          />
        </div>
      )}
    </span>
  );
}

function RangeForm({
  initial,
  onApply,
  onCancel,
}: {
  initial: TimeRange;
  onApply: (range: TimeRange) => void;
  onCancel: () => void;
}) {
  const [start, setStart] = React.useState(() =>
    moment(initial.from).format(LOCAL_INPUT),
  );
  const [end, setEnd] = React.useState(() =>
    moment(initial.to).format(LOCAL_INPUT),
  );
  const from = moment(start, LOCAL_INPUT, true).toDate();
  const to = moment(end, LOCAL_INPUT, true).toDate();
  const error = customRangeError(from, to);
  const startId = React.useId();
  const endId = React.useId();

  return (
    <form
      onSubmit={(e) => {
        e.preventDefault();
        if (!error) onApply({ from, to });
      }}
      className="flex w-[280px] flex-col gap-2.5"
    >
      <div>
        <label
          htmlFor={startId}
          className="mb-1 block text-xs font-medium text-muted-foreground"
        >
          Start time (local)
        </label>
        <Input
          id={startId}
          type="datetime-local"
          value={start}
          onChange={(e) => setStart(e.target.value)}
          error={!!error}
        />
      </div>
      <div>
        <label
          htmlFor={endId}
          className="mb-1 block text-xs font-medium text-muted-foreground"
        >
          End time (local)
        </label>
        <Input
          id={endId}
          type="datetime-local"
          value={end}
          onChange={(e) => setEnd(e.target.value)}
          error={!!error}
        />
      </div>
      {error && (
        <p role="alert" className="m-0 text-[12.5px] text-red-600">
          {error}
        </p>
      )}
      <div className="flex gap-2">
        <Button type="submit" size="sm" colorScheme="primary" disabled={!!error}>
          Apply
        </Button>
        <Button type="button" size="sm" variant="outline" onClick={onCancel}>
          Cancel
        </Button>
      </div>
    </form>
  );
}
