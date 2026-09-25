import moment from "moment";
import * as api from "api-client";

/** The resolution statuses the Failed page covers. */
export const FAILED_STATUSES = [
  api.Statuses.Failed,
  api.Statuses.DeadLettered,
  api.Statuses.Unsupported,
] as const;

/** Chart / badge colour per failure status. DeadLettered is darker and Unsupported muted, so they differ in lightness, not just hue. */
export const STATUS_COLORS: Record<string, string> = {
  Failed: "#C2412E",
  DeadLettered: "#6E1F14",
  Unsupported: "#B9B09A",
};

/** Distinguishable series colours for the per-endpoint split. */
export const ENDPOINT_PALETTE = [
  "#3A6FB0",
  "#C98A1B",
  "#2E8F5E",
  "#6B3FA3",
  "#C2412E",
  "#1F7A8C",
  "#8A5E0F",
  "#A33F6B",
];

export interface PeriodOption {
  value: api.Period;
  label: string;
  spanMinutes: number;
  bucketMinutes: number;
}

/**
 * Range presets. Mirrors FailedImplementation.ResolveWindow on the server so the list's
 * time window matches the chart's buckets exactly.
 */
export const PERIOD_OPTIONS: PeriodOption[] = [
  { value: api.Period._1h, label: "1h", spanMinutes: 60, bucketMinutes: 5 },
  { value: api.Period._12h, label: "12h", spanMinutes: 720, bucketMinutes: 30 },
  { value: api.Period._1d, label: "1d", spanMinutes: 1440, bucketMinutes: 60 },
  { value: api.Period._3d, label: "3d", spanMinutes: 4320, bucketMinutes: 180 },
  {
    value: api.Period._7d,
    label: "7d",
    spanMinutes: 10080,
    bucketMinutes: 360,
  },
  {
    value: api.Period._30d,
    label: "30d",
    spanMinutes: 43200,
    bucketMinutes: 1440,
  },
];

export const DEFAULT_PERIOD = api.Period._7d;

export function periodOption(period: string): PeriodOption {
  return (
    PERIOD_OPTIONS.find((p) => p.value === period) ??
    PERIOD_OPTIONS.find((p) => p.value === DEFAULT_PERIOD)!
  );
}

/** The window a preset covers: it ends at the end of the bucket holding `now`. */
export function resolveWindow(
  period: string,
  now: Date,
): { from: Date; to: Date } {
  const option = periodOption(period);
  const bucketMs = option.bucketMinutes * 60_000;
  const to = Math.floor(now.getTime() / bucketMs) * bucketMs + bucketMs;
  return { from: new Date(to - option.spanMinutes * 60_000), to: new Date(to) };
}

/** URL-backed filter state of the Failed page. Strings only, so it round-trips through the query string. */
export type FailedFilterValues = {
  period: string;
  bucket: string;
  endpointId: string[];
  status: string[];
  eventTypeId: string[];
  eventId: string;
  lastMessageId: string;
  sessionId: string;
  from: string;
  to: string;
  errorText: string;
  view: string;
  split: string;
};

export const EMPTY_FAILED_FILTER: FailedFilterValues = {
  period: DEFAULT_PERIOD,
  bucket: "",
  endpointId: [],
  status: [],
  eventTypeId: [],
  eventId: "",
  lastMessageId: "",
  sessionId: "",
  from: "",
  to: "",
  errorText: "",
  view: "list",
  split: "status",
};

/** The subset of filter fields the filter bar edits (the rest is view state). */
export const SEARCH_FIELDS = [
  "endpointId",
  "status",
  "eventTypeId",
  "eventId",
  "lastMessageId",
  "sessionId",
  "from",
  "to",
  "errorText",
] as const;

/**
 * The API filter for the applied values. `window` narrows UpdatedAt to the chart range (or
 * the selected bar); the histogram is requested without it so the chart keeps the whole range.
 */
export function toFailedSearchFilter(
  values: FailedFilterValues,
  window?: { from: Date; to: Date },
): api.FailedSearchFilter {
  const filter = new api.FailedSearchFilter();
  const text = (v: string) => (v.trim() ? v.trim() : undefined);
  if (values.endpointId.length) filter.endpointIds = [...values.endpointId];
  const statuses = values.status.filter((s): s is api.Statuses =>
    (FAILED_STATUSES as readonly string[]).includes(s),
  );
  if (statuses.length) filter.statuses = statuses;
  if (values.eventTypeId.length) filter.eventTypeId = [...values.eventTypeId];
  filter.eventId = text(values.eventId);
  filter.lastMessageId = text(values.lastMessageId);
  filter.sessionId = text(values.sessionId);
  filter.from = text(values.from);
  filter.to = text(values.to);
  filter.errorText = text(values.errorText);
  if (window) {
    filter.updatedAtFrom = moment(window.from);
    // The API's UpdatedAtTo is inclusive; step back one millisecond to keep buckets half-open.
    filter.updatedAtTo = moment(window.to.getTime() - 1);
  }
  return filter;
}

/** The window of the selected bar, when one is selected and parses. */
export function selectedBucketWindow(
  values: FailedFilterValues,
): { from: Date; to: Date } | undefined {
  if (!values.bucket) return undefined;
  const start = new Date(values.bucket);
  if (Number.isNaN(start.getTime())) return undefined;
  const option = periodOption(values.period);
  return {
    from: start,
    to: new Date(start.getTime() + option.bucketMinutes * 60_000),
  };
}

/**
 * The text a failure is shown by: the handler's error, else what Service Bus recorded when it
 * dead-lettered the message. Mirrors FailedImplementation.ErrorTextOf.
 */
export function errorTextOf(event: api.Event): string | undefined {
  return (
    [
      event.messageContent?.errorContent?.errorText,
      event.deadLetterErrorDescription,
      event.deadLetterReason,
      event.reason,
    ].find((t) => !!t && t.trim().length > 0) ??
    (event.resolutionStatus === api.ResolutionStatus.Unsupported
      ? "Unsupported: no handler for this event type"
      : undefined)
  );
}

/** Deferred-message counts per session from a session-batch response ("eventId_sessionId" ids). */
export function deferredCountsBySession(
  deferredEvents: string[],
): Record<string, number> {
  const counts: Record<string, number> = {};
  for (const id of deferredEvents) {
    const sessionId = id.split("_")[1];
    if (sessionId) counts[sessionId] = (counts[sessionId] ?? 0) + 1;
  }
  return counts;
}

/** Axis / tooltip label for a bucket start. Carries the date once buckets span days. */
export function formatBucket(
  start: moment.Moment | Date | undefined,
  bucketMinutes: number,
  withEnd = false,
): string {
  if (!start) return "";
  const m = moment(start);
  if (bucketMinutes >= 1440) return m.format("DD MMM");
  const label =
    bucketMinutes >= 180 ? m.format("ddd DD HH:mm") : m.format("HH:mm");
  if (!withEnd) return label;
  const end = m.clone().add(bucketMinutes, "minutes");
  return `${m.format("DD/MM HH:mm")}–${end.format("HH:mm")}`;
}

/**
 * Unresolved failures in endpoint status counts. The API's failedCount already includes
 * DeadLettered (Mapper.EndpointStatusCountFromEndpointStateCount), so only Unsupported is added.
 */
export function failureBacklog(counts: api.EndpointStatusCount[]): number {
  return counts.reduce(
    (sum, c) => sum + (c.failedCount ?? 0) + (c.unsupportedCount ?? 0),
    0,
  );
}
