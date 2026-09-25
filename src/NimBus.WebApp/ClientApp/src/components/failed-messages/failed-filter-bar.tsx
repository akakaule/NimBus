import * as React from "react";
import * as api from "api-client";
import { Input } from "components/ui/input";
import { Button } from "components/ui/button";
import { Select } from "components/ui/select";
import { Combobox, type ComboboxOption } from "components/ui/combobox";
import {
  FAILED_STATUSES,
  STATUS_COLORS,
  type FailedFilterValues,
} from "functions/failed-messages.functions";
import { cn } from "lib/utils";

interface FailedFilterBarProps {
  /** The applied filter (URL-driven); the draft resets to it whenever it changes. */
  value: FailedFilterValues;
  /** Per-status counts shown on the status toggles. */
  statusCounts?: Partial<Record<string, number>>;
  onSearch: (next: FailedFilterValues) => void;
  onReset: () => void;
  isLoading: boolean;
}

const label = "mb-1 block text-xs font-medium text-muted-foreground";

/**
 * Filters for the Failed page, shaped like the Messages page's filter bar, plus the error
 * text and the failure-status toggles. The time range is set by the chart above it.
 */
export default function FailedFilterBar({
  value,
  statusCounts,
  onSearch,
  onReset,
  isLoading,
}: FailedFilterBarProps) {
  const [draft, setDraft] = React.useState<FailedFilterValues>(value);
  const [endpoints, setEndpoints] = React.useState<string[]>([]);
  const [eventTypes, setEventTypes] = React.useState<ComboboxOption[]>([]);

  React.useEffect(() => setDraft(value), [value]);

  React.useEffect(() => {
    const client = new api.Client(api.CookieAuth());
    client
      .getEndpointsAll()
      .then(setEndpoints)
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

  const endpointOptions = React.useMemo(
    () => endpoints.map((e) => ({ value: e, label: e })),
    [endpoints],
  );
  const anyEndpoint = React.useMemo(
    () => [{ value: "", label: "Any endpoint" }, ...endpointOptions],
    [endpointOptions],
  );

  const update = <K extends keyof FailedFilterValues>(
    key: K,
    next: FailedFilterValues[K],
  ) => setDraft((d) => ({ ...d, [key]: next }));

  const submit = () => onSearch(draft);
  const onEnter = (e: React.KeyboardEvent) => {
    if (e.key === "Enter") submit();
  };

  const toggleStatus = (status: string) => {
    // No toggles on means all three; switching one off from "all" keeps the other two.
    const current = draft.status.length ? draft.status : [...FAILED_STATUSES];
    const next = current.includes(status)
      ? current.filter((s) => s !== status)
      : [...current, status];
    const normalized = next.length === FAILED_STATUSES.length ? [] : next;
    const updated = { ...draft, status: normalized };
    setDraft(updated);
    onSearch(updated);
  };
  const statusOn = (status: string) =>
    draft.status.length === 0 || draft.status.includes(status);

  return (
    <section
      aria-label="Filters"
      className="rounded-lg border border-border bg-muted p-4"
    >
      <div className="grid grid-cols-2 gap-3 md:grid-cols-3 lg:grid-cols-5">
        <div>
          <span className={label}>Endpoint</span>
          <Combobox
            options={endpointOptions}
            value={draft.endpointId}
            onChange={(v) => update("endpointId", v)}
            placeholder="All endpoints"
            multiple
          />
        </div>
        <div>
          <span className={label}>Event type</span>
          <Combobox
            options={eventTypes}
            value={draft.eventTypeId}
            onChange={(v) => update("eventTypeId", v)}
            placeholder="All event types"
            multiple
          />
        </div>
        <div>
          <label className={label} htmlFor="failed-event-id">
            Event ID
          </label>
          <Input
            id="failed-event-id"
            value={draft.eventId}
            onChange={(e) => update("eventId", e.target.value)}
            onKeyDown={onEnter}
            placeholder="Filter by event ID..."
          />
        </div>
        <div>
          <label className={label} htmlFor="failed-message-id">
            Last message ID
          </label>
          <Input
            id="failed-message-id"
            value={draft.lastMessageId}
            onChange={(e) => update("lastMessageId", e.target.value)}
            onKeyDown={onEnter}
            placeholder="Filter by message ID..."
          />
        </div>
        <div>
          <label className={label} htmlFor="failed-session-id">
            Session ID
          </label>
          <Input
            id="failed-session-id"
            value={draft.sessionId}
            onChange={(e) => update("sessionId", e.target.value)}
            onKeyDown={onEnter}
            placeholder="Filter by session ID..."
          />
        </div>
        <div>
          <label className={label} htmlFor="failed-from">
            From (Publisher)
          </label>
          <Select
            id="failed-from"
            value={draft.from}
            onChange={(e) => update("from", e.target.value)}
            options={anyEndpoint}
          />
        </div>
        <div>
          <label className={label} htmlFor="failed-to">
            To (Subscriber)
          </label>
          <Select
            id="failed-to"
            value={draft.to}
            onChange={(e) => update("to", e.target.value)}
            options={anyEndpoint}
          />
        </div>
        <div>
          <label className={label} htmlFor="failed-error-text">
            Error contains
          </label>
          <Input
            id="failed-error-text"
            value={draft.errorText}
            onChange={(e) => update("errorText", e.target.value)}
            onKeyDown={onEnter}
            placeholder="e.g. 503, timeout"
          />
        </div>
        <div className="col-span-2">
          <span className={label}>Status</span>
          <div
            className="flex flex-wrap gap-2"
            role="group"
            aria-label="Status"
          >
            {FAILED_STATUSES.map((status) => {
              const on = statusOn(status);
              return (
                <button
                  key={status}
                  type="button"
                  aria-pressed={on}
                  onClick={() => toggleStatus(status)}
                  className={cn(
                    "inline-flex h-10 items-center gap-2 rounded-md px-3 text-sm font-semibold",
                    on
                      ? "border border-border-strong bg-card text-foreground"
                      : "border border-dashed border-border-strong text-muted-foreground line-through",
                  )}
                >
                  <span
                    className="inline-block h-2 w-2 rounded-full"
                    style={{
                      background: on ? STATUS_COLORS[status] : "#C9C1AB",
                    }}
                  />
                  {status}
                  {statusCounts?.[status] !== undefined && (
                    <span className="font-mono text-[11px] opacity-80">
                      {statusCounts[status]}
                    </span>
                  )}
                </button>
              );
            })}
          </div>
        </div>
      </div>
      <div className="mt-3 flex gap-2">
        <Button
          onClick={submit}
          disabled={isLoading}
          size="sm"
          colorScheme="primary"
        >
          Search
        </Button>
        <Button
          onClick={onReset}
          disabled={isLoading}
          size="sm"
          variant="outline"
          colorScheme="gray"
        >
          Reset
        </Button>
      </div>
    </section>
  );
}
