import * as React from "react";
import * as api from "api-client";
import { formatMoment } from "functions/endpoint.functions";

interface FlowTimelineProps {
  messages: api.Message[];
  audits: api.MessageAudit[];
}

interface TimelineEntry {
  timestamp: Date;
  type: "message" | "audit";
  messageType?: string;
  auditType?: string;
  from?: string;
  to?: string;
  errorText?: string;
  auditorName?: string;
  comment?: string;
  messageId?: string;
}

const MESSAGE_COLORS: Record<string, { bg: string; border: string; dot: string; label: string }> = {
  EventRequest:        { bg: "bg-blue-50",    border: "border-blue-300",    dot: "bg-blue-500",    label: "Event Request" },
  ResolutionResponse:  { bg: "bg-green-50",   border: "border-green-300",   dot: "bg-green-500",   label: "Completed" },
  ErrorResponse:       { bg: "bg-red-50",     border: "border-red-300",     dot: "bg-red-500",     label: "Error" },
  RetryRequest:        { bg: "bg-amber-50",   border: "border-amber-300",   dot: "bg-amber-500",   label: "Retry" },
  DeferralResponse:    { bg: "bg-purple-50",  border: "border-purple-300",  dot: "bg-purple-500",  label: "Deferred" },
  ResubmissionRequest: { bg: "bg-emerald-50", border: "border-emerald-300", dot: "bg-emerald-500", label: "Resubmission" },
  SkipResponse:        { bg: "bg-gray-50",    border: "border-gray-300",    dot: "bg-gray-400",    label: "Skipped" },
  SkipRequest:         { bg: "bg-gray-50",    border: "border-gray-300",    dot: "bg-gray-400",    label: "Skip Request" },
  ContinuationRequest: { bg: "bg-indigo-50",  border: "border-indigo-300",  dot: "bg-indigo-500",  label: "Continuation" },
  UnsupportedRequest:  { bg: "bg-orange-50",  border: "border-orange-300",  dot: "bg-orange-500",  label: "Unsupported" },
};

const AUDIT_COLOR = { bg: "bg-sky-50", border: "border-sky-200", dot: "bg-sky-400" };

function getMessageColor(messageType: string | undefined) {
  return MESSAGE_COLORS[messageType ?? ""] ?? { bg: "bg-gray-50", border: "border-gray-200", dot: "bg-gray-400", label: messageType ?? "Unknown" };
}

export default function FlowTimeline({ messages, audits }: FlowTimelineProps) {
  const entries = React.useMemo(() => {
    const items: TimelineEntry[] = [];

    for (const msg of messages) {
      const ts = msg.enqueuedTimeUtc;
      if (!ts) continue;
      items.push({
        timestamp: typeof ts.toDate === "function" ? ts.toDate() : new Date(ts as any),
        type: "message",
        messageType: msg.messageType,
        from: msg.originatingFrom ?? msg.from,
        to: msg.to,
        errorText: msg.errorContent?.errorText,
        messageId: msg.messageId,
      });
    }

    for (const audit of audits) {
      const ts = audit.auditTimestamp;
      if (!ts) continue;
      items.push({
        timestamp: typeof ts.toDate === "function" ? ts.toDate() : new Date(ts as any),
        type: "audit",
        auditType: audit.auditType,
        auditorName: audit.auditorName,
        comment: audit.comment,
      });
    }

    items.sort((a, b) => a.timestamp.getTime() - b.timestamp.getTime());
    return items;
  }, [messages, audits]);

  if (entries.length === 0) {
    return (
      <p className="text-muted-foreground text-sm py-8 text-center">
        No message history available for this event.
      </p>
    );
  }

  return (
    <div className="relative pl-8 py-4">
      {/* Vertical line */}
      <div className="absolute left-3.5 top-0 bottom-0 w-0.5 bg-border" />

      {entries.map((entry, i) => {
        if (entry.type === "audit") {
          return (
            <div key={`audit-${i}`} className="relative mb-4">
              <div className={`absolute left-[-20px] top-2 w-3 h-3 rounded-full border-2 border-background ${AUDIT_COLOR.dot}`} />
              <div className={`ml-4 p-3 rounded-md border ${AUDIT_COLOR.bg} ${AUDIT_COLOR.border}`}>
                <div className="flex items-center gap-2 text-xs text-muted-foreground">
                  <span className="font-mono">{entry.timestamp.toISOString().replace("T", " ").slice(0, 23)}</span>
                  <span className="font-semibold text-sky-700">
                    {entry.auditType === "comment" ? "Comment" : entry.auditType}
                  </span>
                  {entry.auditorName && <span>by {entry.auditorName}</span>}
                </div>
                {entry.comment && (
                  <p className="text-sm mt-1 text-sky-900">{entry.comment}</p>
                )}
              </div>
            </div>
          );
        }

        const color = getMessageColor(entry.messageType);

        return (
          <div key={`msg-${i}`} className="relative mb-4">
            <div className={`absolute left-[-20px] top-2 w-3 h-3 rounded-full border-2 border-background ${color.dot}`} />
            <div className={`ml-4 p-3 rounded-md border ${color.bg} ${color.border}`}>
              <div className="flex items-center justify-between gap-2">
                <div className="flex items-center gap-2">
                  <span className={`text-xs font-bold px-2 py-0.5 rounded ${color.dot} text-white`}>
                    {color.label}
                  </span>
                  {entry.from && entry.to && (
                    <span className="text-xs text-muted-foreground">
                      {entry.from} <span className="font-bold">→</span> {entry.to}
                    </span>
                  )}
                </div>
                <span className="text-xs font-mono text-muted-foreground whitespace-nowrap">
                  {entry.timestamp.toISOString().replace("T", " ").slice(0, 23)}
                </span>
              </div>
              {entry.errorText && (
                <p className="text-xs mt-2 text-red-700 bg-red-100 rounded px-2 py-1 font-mono break-all">
                  {entry.errorText}
                </p>
              )}
              {entry.messageId && (
                <p className="text-[10px] mt-1 text-muted-foreground font-mono">
                  MessageId: {entry.messageId}
                </p>
              )}
            </div>
          </div>
        );
      })}
    </div>
  );
}
