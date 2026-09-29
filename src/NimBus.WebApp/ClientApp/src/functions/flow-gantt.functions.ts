import * as api from "api-client";
import {
  formatMessageType,
  messageTypeKey,
} from "functions/message-type.functions";

/** How a flow row is drawn: its colour group. */
export type FlowKind =
  | "request"
  | "retry"
  | "resubmit"
  | "error"
  | "completed"
  | "deferred"
  | "skipped"
  | "handoff"
  | "audit";

/** One row of the end-to-end flow: a message or an audit entry, placed on the time axis. */
export interface FlowRow {
  key: string;
  at: Date;
  /** Milliseconds since the first row. */
  offsetMs: number;
  /**
   * For a request, the time until the next response (queue plus processing). Messages carry
   * no duration of their own, so everything else is a point.
   */
  durationMs?: number;
  kind: FlowKind;
  label: string;
  /** "from → to" for a message, "by <auditor>" for an audit. */
  route: string;
  errorText?: string;
  stackTrace?: string;
  message?: api.Message;
  audit?: api.MessageAudit;
}

/** Rows beyond this many are left to Event Details. */
export const MAX_FLOW_ROWS = 50;

/** Row colours; they differ in lightness as well as hue. */
export const FLOW_COLORS: Record<FlowKind, string> = {
  request: "#3A6FB0",
  retry: "#C98A1B",
  resubmit: "#2E8F5E",
  error: "#C2412E",
  completed: "#1F6B45",
  deferred: "#6B3FA3",
  skipped: "#8A8473",
  handoff: "#1F7A8C",
  audit: "#4A463D",
};

export const FLOW_LEGEND: { kind: FlowKind; label: string }[] = [
  { kind: "request", label: "Request" },
  { kind: "retry", label: "Retry" },
  { kind: "resubmit", label: "Resubmission" },
  { kind: "error", label: "Error" },
  { kind: "completed", label: "Completed" },
  { kind: "audit", label: "Audit" },
];

const KIND_BY_TYPE: Record<string, FlowKind> = {
  eventRequest: "request",
  continuationRequest: "request",
  unsupportedRequest: "request",
  skipRequest: "request",
  retryRequest: "retry",
  resubmissionRequest: "resubmit",
  handoffCompletedRequest: "handoff",
  handoffFailedRequest: "handoff",
  errorResponse: "error",
  resolutionResponse: "completed",
  deferralResponse: "deferred",
  skipResponse: "skipped",
  pendingHandoffResponse: "handoff",
};

const toDate = (m: moment.Moment | undefined): Date | undefined =>
  m ? new Date(m.valueOf()) : undefined;

const isRequest = (key: string) => key.endsWith("Request");
const isResponse = (key: string) => key.endsWith("Response");

// Reading an event is audited too, but it is not part of the message's flow.
const isReadAudit = (type: string | undefined) =>
  !!type && /^(get|search)/i.test(type);

const auditLabel = (type: string | undefined) => {
  if (!type) return "Audit";
  const words = type.replace(/([a-z])([A-Z])/g, "$1 $2").toLowerCase();
  return `Audit · ${words[0].toUpperCase()}${words.slice(1)}`;
};

/**
 * The end-to-end flow of one event, oldest first: its history messages and write audits on a
 * shared time axis. A request is drawn as a span up to the next response, retries are numbered,
 * and everything else is a point.
 */
export function buildFlowRows(
  messages: api.Message[],
  audits: api.MessageAudit[],
): { rows: FlowRow[]; spanMs: number; hidden: number } {
  type Entry = Omit<FlowRow, "offsetMs"> & { typeKey?: string };
  const entries: Entry[] = [];

  messages.forEach((m, i) => {
    const at = toDate(m.enqueuedTimeUtc);
    if (!at) return;
    const typeKey = messageTypeKey(m.messageType ?? "");
    entries.push({
      key: `m-${m.messageId ?? i}-${i}`,
      at,
      kind: KIND_BY_TYPE[typeKey] ?? "request",
      label: formatMessageType(m.messageType) || "Message",
      route: [m.from || m.originatingFrom, m.to].filter(Boolean).join(" → "),
      errorText: m.errorContent?.errorText,
      stackTrace: m.errorContent?.exceptionStackTrace,
      message: m,
      typeKey,
    });
  });
  audits.forEach((a, i) => {
    const at = toDate(a.auditTimestamp);
    if (!at || isReadAudit(a.auditType)) return;
    entries.push({
      key: `a-${i}`,
      at,
      kind: "audit",
      label: auditLabel(a.auditType),
      route: a.auditorName ? `by ${a.auditorName}` : "",
      audit: a,
    });
  });

  if (entries.length === 0) return { rows: [], spanMs: 0, hidden: 0 };
  entries.sort((a, b) => a.at.getTime() - b.at.getTime());

  let retries = 0;
  entries.forEach((entry, i) => {
    if (entry.typeKey === "retryRequest") entry.label = `Retry ${++retries}`;
    if (!entry.typeKey || !isRequest(entry.typeKey)) return;
    // The span ends at the first response before the next request.
    for (const next of entries.slice(i + 1)) {
      if (!next.typeKey) continue;
      if (isRequest(next.typeKey)) break;
      if (isResponse(next.typeKey)) {
        entry.durationMs = next.at.getTime() - entry.at.getTime();
        break;
      }
    }
  });

  const first = entries[0].at.getTime();
  const shown = entries.slice(0, MAX_FLOW_ROWS);
  const rows = shown.map(({ typeKey: _typeKey, ...entry }) => ({
    ...entry,
    offsetMs: entry.at.getTime() - first,
  }));
  const spanMs = Math.max(
    ...rows.map((r) => r.offsetMs + (r.durationMs ?? 0)),
  );
  return { rows, spanMs, hidden: entries.length - shown.length };
}

const STEPS = [
  100, 200, 500, 1_000, 2_000, 5_000, 10_000, 15_000, 30_000, 60_000, 120_000,
  300_000, 600_000, 900_000, 1_800_000, 3_600_000, 7_200_000, 21_600_000,
  43_200_000, 86_400_000,
];

/** Round axis ticks from 0: the smallest step that gives at most five intervals. */
export function axisTicks(spanMs: number): number[] {
  if (spanMs <= 0) return [0];
  const step =
    STEPS.find((s) => Math.floor(spanMs / s) <= 4) ?? STEPS[STEPS.length - 1];
  const ticks: number[] = [];
  for (let t = 0; t <= spanMs; t += step) ticks.push(t);
  return ticks;
}

/** A duration or offset as a stopwatch reads it: "41 ms", "1.24 s", "2m 05s", "1h". */
export function formatSpan(ms: number): string {
  if (ms <= 0) return "0";
  if (ms < 1_000) return `${Math.round(ms)} ms`;
  if (ms < 60_000)
    return ms % 1_000 === 0 ? `${ms / 1_000} s` : `${(ms / 1_000).toFixed(2)} s`;
  if (ms < 3_600_000) {
    const minutes = Math.floor(ms / 60_000);
    const seconds = Math.round((ms % 60_000) / 1_000);
    return seconds ? `${minutes}m ${String(seconds).padStart(2, "0")}s` : `${minutes}m`;
  }
  const hours = Math.floor(ms / 3_600_000);
  const minutes = Math.round((ms % 3_600_000) / 60_000);
  return minutes ? `${hours}h ${String(minutes).padStart(2, "0")}m` : `${hours}h`;
}
