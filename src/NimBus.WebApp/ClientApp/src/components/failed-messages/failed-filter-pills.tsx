import * as React from "react";
import * as api from "api-client";
import { Button } from "components/ui/button";
import { Checkbox } from "components/ui/checkbox";
import { Combobox, type ComboboxOption } from "components/ui/combobox";
import { Select } from "components/ui/select";
import {
  FAILED_STATUSES,
  PERIOD_OPTIONS,
  STATUS_COLORS,
  parseSearchQuery,
  periodOption,
  searchQueryOf,
  toggleStatus,
  windowParams,
  type FailedFilterValues,
} from "functions/failed-messages.functions";
import { cn } from "lib/utils";

const PERIOD_LABELS: Record<string, string> = {
  "1h": "Last hour",
  "12h": "Last 12 hours",
  "1d": "Last 24 hours",
  "3d": "Last 3 days",
  "7d": "Last 7 days",
  "30d": "Last 30 days",
};

/** The time pill's label for a period option ("Last 7 days"). */
export const periodLabel = (label: string) =>
  PERIOD_LABELS[label] ?? `Last ${label}`;

const pillClass = (active: boolean) =>
  cn(
    "inline-flex h-8 items-center gap-1 whitespace-nowrap rounded-full px-3.5 text-[13px] text-foreground transition-colors",
    active
      ? "bg-primary-tint ring-1 ring-primary dark:bg-primary-900/40"
      : "bg-muted hover:bg-border",
  );

const ChevronDown = () => (
  <svg
    width="12"
    height="12"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth="2.4"
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
  >
    <path d="M6 9l6 6 6-6" />
  </svg>
);

interface PillPopoverProps {
  /** Accessible name of the pill's button. */
  label: string;
  /** Visible pill text. */
  children: React.ReactNode;
  /** Highlights a pill whose filter narrows the results. */
  active?: boolean;
  /** Renders the popover content; call `close` to dismiss it. */
  content: (close: () => void) => React.ReactNode;
  /** Adds a remove (x) button next to the pill. */
  onRemove?: () => void;
  className?: string;
}

/** A filter pill (Application Insights style) that opens a small popover below it. */
export function PillPopover({
  label,
  children,
  active = false,
  content,
  onRemove,
  className,
}: PillPopoverProps) {
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
      <span className={cn(pillClass(active), onRemove && "pr-1", className)}>
        <button
          ref={trigger}
          type="button"
          aria-label={label}
          aria-haspopup="dialog"
          aria-expanded={open}
          onClick={() => setOpen((o) => !o)}
          className="inline-flex items-center gap-1"
        >
          {children}
        </button>
        {onRemove && (
          <button
            type="button"
            aria-label={`Remove ${label}`}
            onClick={onRemove}
            className="ml-1 inline-flex h-6 w-6 items-center justify-center rounded-full text-muted-foreground hover:bg-card hover:text-foreground"
          >
            <svg
              width="11"
              height="11"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2.6"
              strokeLinecap="round"
              aria-hidden="true"
            >
              <path d="M6 6l12 12M18 6L6 18" />
            </svg>
          </button>
        )}
      </span>
      {open && (
        <div
          role="dialog"
          aria-label={label}
          className="absolute left-0 top-full z-40 mt-1.5 min-w-[260px] rounded-nb-md border border-border-strong bg-card p-3 text-sm shadow-nb-lg animate-fade-in"
        >
          {content(close)}
        </div>
      )}
    </span>
  );
}

function useFilterOptions() {
  const [endpoints, setEndpoints] = React.useState<ComboboxOption[]>([]);
  const [eventTypes, setEventTypes] = React.useState<ComboboxOption[]>([]);
  React.useEffect(() => {
    const client = new api.Client(api.CookieAuth());
    client
      .getEndpointsAll()
      .then((ids) => setEndpoints(ids.map((e) => ({ value: e, label: e }))))
      .catch(() => setEndpoints([]));
    client
      .getEventTypes()
      .then((types) =>
        setEventTypes(
          types
            .map((t) => t.name)
            .filter((name): name is string => Boolean(name))
            .map((name) => ({ value: name, label: name })),
        ),
      )
      .catch(() => setEventTypes([]));
  }, []);
  return { endpoints, eventTypes };
}

const summary = (values: string[], none = "All selected") =>
  values.length === 0
    ? none
    : values.length === 1
      ? values[0]
      : `${values[0]} +${values.length - 1}`;

function MultiSelectContent({
  options,
  value,
  placeholder,
  onApply,
  close,
}: {
  options: ComboboxOption[];
  value: string[];
  placeholder: string;
  onApply: (next: string[]) => void;
  close: () => void;
}) {
  const [draft, setDraft] = React.useState(value);
  return (
    <div className="flex w-[320px] flex-col gap-3">
      <Combobox
        options={options}
        value={draft}
        onChange={setDraft}
        placeholder={placeholder}
        multiple
      />
      <div className="flex gap-2">
        <Button
          size="sm"
          colorScheme="primary"
          onClick={() => {
            onApply(draft);
            close();
          }}
        >
          Apply
        </Button>
        <Button
          size="sm"
          variant="outline"
          colorScheme="gray"
          onClick={() => {
            onApply([]);
            close();
          }}
        >
          Clear
        </Button>
      </div>
    </div>
  );
}

function EndpointSelectContent({
  label,
  options,
  value,
  onApply,
  close,
}: {
  label: string;
  options: ComboboxOption[];
  value: string;
  onApply: (next: string) => void;
  close: () => void;
}) {
  const [draft, setDraft] = React.useState(value);
  const id = React.useId();
  return (
    <div className="flex w-[280px] flex-col gap-3">
      <label
        htmlFor={id}
        className="text-xs font-medium text-muted-foreground"
      >
        {label}
      </label>
      <Select
        id={id}
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        options={[{ value: "", label: "Any endpoint" }, ...options]}
      />
      <Button
        size="sm"
        colorScheme="primary"
        onClick={() => {
          onApply(draft);
          close();
        }}
      >
        Apply
      </Button>
    </div>
  );
}

export interface FailedFilterPillsProps {
  /** The applied filter (URL-driven). */
  value: FailedFilterValues;
  /** Per-status counts shown in the status pill. */
  statusCounts?: Partial<Record<string, number>>;
  /** Applies a change to the filter. */
  onApply: (patch: Partial<FailedFilterValues>) => void;
  onReset: () => void;
}

/**
 * The Failed page's filter row, after Application Insights' transaction search: time, view
 * and field filters as pills, with the less-used publisher/subscriber filters behind (+).
 */
export default function FailedFilterPills({
  value,
  statusCounts,
  onApply,
  onReset,
}: FailedFilterPillsProps) {
  const { endpoints, eventTypes } = useFilterOptions();
  const option = periodOption(value.period);
  const statusOn = (status: string) =>
    value.status.length === 0 || value.status.includes(status);
  const filtered =
    value.endpointId.length > 0 ||
    value.eventTypeId.length > 0 ||
    value.status.length > 0 ||
    !!value.from ||
    !!value.to ||
    !!searchQueryOf(value);

  return (
    <div
      className="flex flex-wrap items-center gap-2"
      role="group"
      aria-label="Filters"
    >
      <PillPopover
        label="Time range"
        content={(close) => (
          <div className="flex min-w-[180px] flex-col">
            {PERIOD_OPTIONS.map((p) => (
              <button
                key={p.value}
                type="button"
                aria-pressed={p.value === option.value}
                onClick={() => {
                  onApply({ period: p.value, ...windowParams(undefined) });
                  close();
                }}
                className={cn(
                  "rounded-nb-sm px-2.5 py-1.5 text-left hover:bg-muted",
                  p.value === option.value && "font-bold text-primary-700",
                )}
              >
                {periodLabel(p.label)}
              </button>
            ))}
          </div>
        )}
      >
        Local time: <b className="font-bold">{periodLabel(option.label)}</b>
        <ChevronDown />
      </PillPopover>

      <PillPopover
        label="View as"
        content={(close) => (
          <div className="flex min-w-[180px] flex-col">
            {[
              ["items", "Individual items"],
              ["table", "Table"],
            ].map(([key, text]) => (
              <button
                key={key}
                type="button"
                aria-pressed={value.display === key}
                onClick={() => {
                  onApply({ display: key });
                  close();
                }}
                className={cn(
                  "rounded-nb-sm px-2.5 py-1.5 text-left hover:bg-muted",
                  value.display === key && "font-bold text-primary-700",
                )}
              >
                {text}
              </button>
            ))}
          </div>
        )}
      >
        View as:{" "}
        <b className="font-bold">
          {value.display === "table" ? "Table" : "Individual items"}
        </b>
        <ChevronDown />
      </PillPopover>

      <PillPopover
        label="Status"
        active={value.status.length > 0}
        content={() => (
          <div className="flex min-w-[220px] flex-col gap-1">
            {FAILED_STATUSES.map((status) => (
              <label
                key={status}
                className="flex cursor-pointer items-center gap-2.5 rounded-nb-sm px-2 py-1.5 hover:bg-muted"
              >
                <Checkbox
                  checked={statusOn(status)}
                  onChange={() =>
                    onApply({ status: toggleStatus(value.status, status) })
                  }
                />
                <span
                  className="inline-block h-2 w-2 rounded-full"
                  style={{ background: STATUS_COLORS[status] }}
                />
                <span className="flex-1">{status}</span>
                {statusCounts?.[status] !== undefined && (
                  <span className="font-mono text-[11px] text-muted-foreground">
                    {statusCounts[status]}
                  </span>
                )}
              </label>
            ))}
          </div>
        )}
      >
        Status = <b className="font-bold">{summary(value.status)}</b>
        <ChevronDown />
      </PillPopover>

      <PillPopover
        label="Endpoint"
        active={value.endpointId.length > 0}
        content={(close) => (
          <MultiSelectContent
            options={endpoints}
            value={value.endpointId}
            placeholder="All endpoints"
            onApply={(endpointId) => onApply({ endpointId })}
            close={close}
          />
        )}
      >
        Endpoint = <b className="font-bold">{summary(value.endpointId)}</b>
        <ChevronDown />
      </PillPopover>

      <PillPopover
        label="Event type"
        active={value.eventTypeId.length > 0}
        content={(close) => (
          <MultiSelectContent
            options={eventTypes}
            value={value.eventTypeId}
            placeholder="All event types"
            onApply={(eventTypeId) => onApply({ eventTypeId })}
            close={close}
          />
        )}
      >
        Event type = <b className="font-bold">{summary(value.eventTypeId)}</b>
        <ChevronDown />
      </PillPopover>

      {(["from", "to"] as const)
        .filter((field) => !!value[field])
        .map((field) => {
          const text = field === "from" ? "From (publisher)" : "To (subscriber)";
          return (
            <PillPopover
              key={field}
              label={text}
              active
              onRemove={() => onApply({ [field]: "" })}
              content={(close) => (
                <EndpointSelectContent
                  label={text}
                  options={endpoints}
                  value={value[field]}
                  onApply={(next) => onApply({ [field]: next })}
                  close={close}
                />
              )}
            >
              {field === "from" ? "From" : "To"} ={" "}
              <b className="font-bold">{value[field]}</b>
            </PillPopover>
          );
        })}

      <PillPopover
        label="Add filter"
        className="px-3 text-primary-700"
        content={(close) => (
          <AddFilterContent
            options={endpoints}
            value={value}
            onApply={onApply}
            close={close}
          />
        )}
      >
        <svg
          width="18"
          height="18"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeWidth="2"
          strokeLinecap="round"
          strokeLinejoin="round"
          aria-hidden="true"
        >
          <path d="M3 5h12l-4.5 6v6l-3 2v-8z" />
          <path d="M19 4v6M16 7h6" />
        </svg>
      </PillPopover>

      {filtered && (
        <button
          type="button"
          onClick={onReset}
          className="ml-1 text-[13px] font-semibold text-primary-700 hover:underline"
        >
          Reset filters
        </button>
      )}
    </div>
  );
}

function AddFilterContent({
  options,
  value,
  onApply,
  close,
}: {
  options: ComboboxOption[];
  value: FailedFilterValues;
  onApply: (patch: Partial<FailedFilterValues>) => void;
  close: () => void;
}) {
  const [from, setFrom] = React.useState(value.from);
  const [to, setTo] = React.useState(value.to);
  const any = [{ value: "", label: "Any endpoint" }, ...options];
  const fromId = React.useId();
  const toId = React.useId();
  return (
    <div className="flex w-[300px] flex-col gap-3">
      <div>
        <label
          htmlFor={fromId}
          className="mb-1 block text-xs font-medium text-muted-foreground"
        >
          From (publisher)
        </label>
        <Select
          id={fromId}
          value={from}
          onChange={(e) => setFrom(e.target.value)}
          options={any}
        />
      </div>
      <div>
        <label
          htmlFor={toId}
          className="mb-1 block text-xs font-medium text-muted-foreground"
        >
          To (subscriber)
        </label>
        <Select
          id={toId}
          value={to}
          onChange={(e) => setTo(e.target.value)}
          options={any}
        />
      </div>
      <p className="m-0 text-[12px] text-muted-foreground">
        Event, message and session IDs and error text go in the search box.
      </p>
      <Button
        size="sm"
        colorScheme="primary"
        onClick={() => {
          onApply({ from, to });
          close();
        }}
      >
        Apply
      </Button>
    </div>
  );
}

export interface FailedSearchBoxProps {
  value: FailedFilterValues;
  onApply: (patch: Partial<FailedFilterValues>) => void;
}

/**
 * One search box for the ID and error-text filters. `event:`, `message:` and `session:` set
 * those fields, a bare GUID finds an event ID, and anything else searches the error text.
 */
export function FailedSearchBox({ value, onApply }: FailedSearchBoxProps) {
  const applied = searchQueryOf(value);
  const [draft, setDraft] = React.useState(applied);
  React.useEffect(() => setDraft(applied), [applied]);
  const submit = (text: string) => onApply(parseSearchQuery(text));

  return (
    <div className="flex h-10 items-center gap-2.5 rounded-nb-sm border border-border-strong bg-card px-3 focus-within:border-primary focus-within:ring-2 focus-within:ring-primary-200">
      <svg
        width="16"
        height="16"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.8"
        strokeLinecap="round"
        aria-hidden="true"
        className="shrink-0 text-muted-foreground"
      >
        <circle cx="11" cy="11" r="7" />
        <path d="M20 20l-3.5-3.5" />
      </svg>
      <input
        type="search"
        aria-label="Search failures"
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Enter") submit(draft);
        }}
        placeholder="Search error text, e.g. 503 or timeout, or event:… message:… session:… (a bare GUID finds an event)"
        className="h-full min-w-0 flex-1 bg-transparent text-sm text-foreground outline-none placeholder:text-muted-foreground"
      />
      {applied && (
        <button
          type="button"
          aria-label="Clear search"
          onClick={() => submit("")}
          className="text-[12.5px] font-semibold text-muted-foreground hover:text-foreground"
        >
          Clear
        </button>
      )}
    </div>
  );
}
