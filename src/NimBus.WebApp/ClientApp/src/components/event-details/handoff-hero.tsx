import * as React from "react";
import type * as moment from "moment";
import { Button } from "components/ui/button";
import { formatMoment } from "functions/endpoint.functions";

// Variant B — "Hero state card". A banded header that lives *inside* the event
// card (rather than as a separate left column), surfacing the handoff state up
// front with the operator actions one click away. Three states share one
// component: pending (amber, actionable), settled (green), failed (red).
// Mirrors the K&L "Event Details (Variant B)" handoff design.

export type HandoffState = "pending" | "settled" | "failed";

interface IHandoffHeroProps {
  state: HandoffState;
  /** Pending: the reason the message is parked awaiting external work. */
  reason?: string;
  externalJobId?: string;
  expectedBy?: moment.Moment;
  /** Settled: optional operator note recorded on completion. */
  settledNote?: string;
  /** Failed: the failure reason supplied by the operator. */
  failedReason?: string;
  /** Settled/failed: who resolved it (operator name). */
  resolvedBy?: string;
  /** Open the Complete-handoff modal. */
  onComplete?: () => void;
  /** Open the Fail-handoff modal. */
  onFail?: () => void;
  /** Disable the action buttons while a settlement is in flight. */
  busy?: boolean;
}

// Per-state palette. The arbitrary border hex values match the design's
// hand-tuned tints (warning #EAD6A0, success #C9E5D5, danger #F2C8C0) which
// sit between the -50 fill and the DEFAULT ink in each status ramp.
const STATE_STYLE: Record<
  HandoffState,
  { rail: string; icon: string; pip: string; pipText: string; title: string; pipLabel: string }
> = {
  pending: {
    rail: "bg-status-warning",
    icon: "bg-status-warning-50 text-status-warning border-[#EAD6A0]",
    pip: "bg-status-warning-50 border-[#EAD6A0]",
    pipText: "text-status-warning-ink",
    title: "Pending handoff",
    pipLabel: "awaiting external",
  },
  settled: {
    rail: "bg-status-success",
    icon: "bg-status-success-50 text-status-success border-[#C9E5D5]",
    pip: "bg-status-success-50 border-[#C9E5D5]",
    pipText: "text-status-success-ink",
    title: "Handoff completed",
    pipLabel: "settled",
  },
  failed: {
    rail: "bg-status-danger",
    icon: "bg-status-danger-50 text-status-danger border-[#F2C8C0]",
    pip: "bg-status-danger-50 border-[#F2C8C0]",
    pipText: "text-status-danger-ink",
    title: "Handoff failed",
    pipLabel: "blocked",
  },
};

function ClockIcon() {
  return (
    <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <circle cx="12" cy="12" r="10" />
      <polyline points="12 6 12 12 16 14" />
    </svg>
  );
}
function CheckIcon() {
  return (
    <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
      <polyline points="20 6 9 17 4 12" />
    </svg>
  );
}
function XIcon() {
  return (
    <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
      <line x1="18" y1="6" x2="6" y2="18" />
      <line x1="6" y1="6" x2="18" y2="18" />
    </svg>
  );
}

// Whole-minute countdown to the deadline. Cosmetic — recomputed each render and
// once a minute via a tick so a parked page stays roughly honest. Past-due
// reads "overdue" in danger ink.
function useCountdown(expectedBy?: moment.Moment): { text: string; overdue: boolean } {
  const [, setTick] = React.useState(0);
  React.useEffect(() => {
    const id = setInterval(() => setTick((t) => t + 1), 60_000);
    return () => clearInterval(id);
  }, []);
  if (!expectedBy) return { text: "", overdue: false };
  const mins = Math.round((expectedBy.valueOf() - Date.now()) / 60_000);
  if (mins <= 0) return { text: "overdue", overdue: true };
  return { text: `${mins} m to deadline`, overdue: false };
}

export default function HandoffHero(props: IHandoffHeroProps) {
  const s = STATE_STYLE[props.state];
  const countdown = useCountdown(props.expectedBy);

  return (
    <div className="relative bg-card border-b border-border px-[22px] py-[18px] grid grid-cols-[auto_1fr_auto] gap-[18px] items-center">
      {/* Left status rail */}
      <span className={`absolute top-0 left-0 h-full w-1 ${s.rail}`} aria-hidden="true" />

      {/* Icon */}
      <span className={`relative inline-flex h-[42px] w-[42px] shrink-0 items-center justify-center rounded-[10px] border ${s.icon}`}>
        {props.state === "pending" ? <ClockIcon /> : props.state === "settled" ? <CheckIcon /> : <XIcon />}
        {props.state === "pending" && (
          <span className="absolute -inset-0.5 rounded-[11px] border-2 border-status-warning opacity-40 animate-ping" aria-hidden="true" />
        )}
      </span>

      {/* Body */}
      <div className="min-w-0">
        <h4 className="m-0 flex items-center gap-2 text-[15px] font-bold text-foreground">
          {s.title}
          <span className={`rounded-full border px-[7px] py-[2px] font-mono text-[10px] font-semibold ${s.pip} ${s.pipText}`}>
            {s.pipLabel}
          </span>
        </h4>

        {props.state === "pending" && (
          <>
            <p className="mt-[3px] text-[13px] text-muted-foreground">
              <b className="font-semibold text-foreground">Reason ·</b>{" "}
              {props.reason || "Awaiting external work"}
            </p>
            <div className="mt-[6px] flex flex-wrap gap-x-[18px] gap-y-1 font-mono text-[11.5px] text-muted-foreground">
              {props.externalJobId && (
                <span>
                  <b className="font-semibold text-foreground/80">External job</b> {props.externalJobId}
                </span>
              )}
              {props.expectedBy && (
                <span>
                  <b className="font-semibold text-foreground/80">Expected by</b> {formatMoment(props.expectedBy)}
                  {countdown.text && (
                    <>
                      {" · "}
                      <span className="font-bold text-status-danger">{countdown.text}</span>
                    </>
                  )}
                </span>
              )}
            </div>
          </>
        )}

        {props.state === "settled" && (
          <>
            <p className="mt-[3px] text-[13px] text-muted-foreground">
              <b className="font-semibold text-foreground">Resolved by ·</b> {props.resolvedBy || "operator"} · just now
            </p>
            <div className="mt-[6px] font-mono text-[11.5px] text-muted-foreground">
              <span>
                <b className="font-semibold text-foreground/80">{props.settledNote ? "Note" : "External job"}</b>{" "}
                {props.settledNote || (props.externalJobId ? `${props.externalJobId} finished cleanly` : "completed cleanly")}
              </span>
            </div>
          </>
        )}

        {props.state === "failed" && (
          <>
            <p className="mt-[3px] text-[13px] text-muted-foreground">
              <b className="font-semibold text-foreground">Failed by ·</b> {props.resolvedBy || "operator"} · just now
            </p>
            <div className="mt-[6px] font-mono text-[11.5px] text-muted-foreground">
              <span>
                <b className="font-semibold text-foreground/80">Reason</b> {props.failedReason || "—"}
              </span>
            </div>
          </>
        )}
      </div>

      {/* Actions — pending only */}
      {props.state === "pending" && (
        <div className="flex shrink-0 gap-2">
          <Button
            size="sm"
            colorScheme="blue"
            disabled={props.busy}
            onClick={props.onComplete}
            leftIcon={
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
                <polyline points="20 6 9 17 4 12" />
              </svg>
            }
          >
            Complete handoff
          </Button>
          <Button
            size="sm"
            colorScheme="red"
            variant="outline"
            disabled={props.busy}
            onClick={props.onFail}
            leftIcon={
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
                <line x1="18" y1="6" x2="6" y2="18" />
                <line x1="6" y1="6" x2="18" y2="18" />
              </svg>
            }
          >
            Fail
          </Button>
        </div>
      )}
    </div>
  );
}
