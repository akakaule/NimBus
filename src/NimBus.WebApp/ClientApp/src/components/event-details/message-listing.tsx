import * as React from "react";
import * as api from "api-client";
import { Badge } from "components/ui/badge";
import { Button } from "components/ui/button";
import {
  Modal,
  ModalHeader,
  ModalBody,
  ModalFooter,
} from "components/ui/modal";
import { Select } from "components/ui/select";
import { Textarea } from "components/ui/textarea";
import { CodeBlock } from "components/ui/code-block";
import { TimingBar } from "components/ui/timing-bar";
import {
  PropertyList,
  PropertySection,
  PropertyRow,
} from "components/ui/property-list";
import HandoffHero, {
  type HandoffState,
} from "components/event-details/handoff-hero";
import { useToast } from "components/ui/toast";
import { formatMoment } from "functions/endpoint.functions";
import { Link } from "react-router-dom";
import { useEffect, useState } from "react";

// Threshold above which we surface an "Above P95" callout next to the timing
// bar. The number is a UI heuristic, not a server-supplied value — the design
// flags slow events so the operator can investigate (rec §09 event-details).
const SLOW_PROCESSING_MS = 1000;

function statusToBadgeVariant(
  status: string | undefined,
):
  | "completed"
  | "failed"
  | "deferred"
  | "deadlettered"
  | "skipped"
  | "unsupported"
  | "pending"
  | "default" {
  switch (status?.toLowerCase()) {
    case "completed":
      return "completed";
    case "failed":
      return "failed";
    case "deferred":
      return "deferred";
    case "deadlettered":
      return "deadlettered";
    case "skipped":
      return "skipped";
    case "unsupported":
      return "unsupported";
    case "pending":
      return "pending";
    default:
      return "default";
  }
}

// Format a millisecond duration for display: "—" when null, "Xms" under 1s,
// "X.Ys" otherwise. Used for Queue Time / Processing Time on the detail page.
function formatDurationMs(ms: number | null | undefined): string {
  if (ms === null || ms === undefined) return "—";
  if (ms < 1000) return `${ms}ms`;
  return `${(ms / 1000).toFixed(2)}s`;
}

// Pretty-print JSON when valid; pass through verbatim otherwise so we never
// hide bad payloads from operators.
function safeFormatJson(raw: string): string {
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
}

interface IMessageListingProps {
  eventDetails: api.Event | undefined;
  eventTypes: api.EventType[];
  skipEvent: (eventId: string, messageId: string) => Promise<void>;
  resubmitEvent: (eventId: string, messageId: string) => Promise<void>;
  resubmitEventWithChanges: (
    eventId: string,
    messageId: string,
    body: api.ResubmitWithChanges,
  ) => Promise<void>;
  deleteEvent?: () => Promise<void>;
  reprocessDeferred?: () => Promise<void>;
  completeHandoff?: (
    endpointId: string,
    eventId: string,
    messageId: string,
    note?: string,
  ) => Promise<void>;
  failHandoff?: (
    endpointId: string,
    eventId: string,
    messageId: string,
    reason: string,
    errorType?: string,
  ) => Promise<void>;
  onCommentAdded?: () => void;
  /**
   * Spec 006: id of the event currently blocking this session, parsed by the
   * page from the most recent deferral error text. Drives the "Blocked by" row
   * in the Properties panel. Omit when not deferred or no GUID could be parsed.
   */
  blockedByEventId?: string;
}

interface IButtonState {
  isDisabled: boolean;
  text: string;
}

export default function MessageListing(props: IMessageListingProps) {
  const [showErrorDetails, setShowErrorDetails] = useState(false);
  const handleErrorDetailsToggle = () => setShowErrorDetails(!showErrorDetails);
  const [isOpen, setIsOpen] = useState(false);
  const onOpen = () => setIsOpen(true);
  const onClose = () => setIsOpen(false);
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const deleteBtn: IButtonState = { isDisabled: false, text: "Delete" };
  const [deleteButton, setDeleteButton] = useState(deleteBtn);
  const [textAreaValue, setTextAreaValue] = useState(
    props.eventDetails?.messageContent?.eventContent?.eventJson,
  );
  const [eventTypeIdValue, setEventTypeIdValue] = useState(
    props.eventDetails?.eventTypeId,
  );

  const { addToast } = useToast();

  // Handoff hero (Variant B). `heroOverride` lets the card flip to settled/
  // failed the instant the operator acts — the underlying Pending → Completed/
  // Failed transition is asynchronous (the subscriber processes the published
  // control message), so the override reflects operator intent until the event
  // document catches up on the next load.
  const [heroOverride, setHeroOverride] = useState<{
    state: HandoffState;
    note?: string;
    reason?: string;
  } | null>(null);
  const [completeOpen, setCompleteOpen] = useState(false);
  const [failOpen, setFailOpen] = useState(false);
  const [handoffNote, setHandoffNote] = useState("");
  const [failReason, setFailReason] = useState("");
  const [handoffBusy, setHandoffBusy] = useState(false);
  const [operatorName, setOperatorName] = useState<string>("operator");
  const [piiRevealed, setPiiRevealed] = useState(false);

  useEffect(() => {
    setTextAreaValue(
      props.eventDetails?.messageContent?.eventContent?.eventJson,
    );
    setShowErrorDetails(false);
    // Reset handoff UI when navigating to a different event.
    setHeroOverride(null);
    setPiiRevealed(false);
  }, [props.eventDetails]);

  useEffect(() => {
    let active = true;
    const client = new api.Client(api.CookieAuth());
    client
      .getMe()
      .then((me) => {
        if (active && me?.name) setOperatorName(me.name);
      })
      .catch(() => {
        /* fallback to "operator" */
      });
    return () => {
      active = false;
    };
  }, []);

  const resubmitWithChanges: IButtonState = {
    isDisabled: false,
    text: "Resubmit with changes",
  };
  const [resubmitWithChangesButton, setResubmitWithChangesButton] =
    useState(resubmitWithChanges);

  const resubmit: IButtonState = { isDisabled: false, text: "Resubmit" };
  const [resubmitButton, setResubmitButton] = useState(resubmit);

  const skip: IButtonState = { isDisabled: false, text: "Skip" };
  const [skipButton, setSkipButton] = useState(skip);

  const handleInputChange = (e: React.ChangeEvent<HTMLTextAreaElement>) => {
    const inputValue = e.target.value;
    const sanitizedValue = inputValue.replace(/\n/g, "");
    setTextAreaValue(sanitizedValue);
  };

  const handleEventTypeIdChange = (e: React.ChangeEvent<HTMLSelectElement>) => {
    setEventTypeIdValue(e.target.value);
  };

  const isFailedMessage = (status: string | undefined): boolean => {
    if (!status) return false;
    const lowerStatus = status.toLowerCase();
    return (
      lowerStatus === "failed" ||
      lowerStatus === "unsupported" ||
      lowerStatus === "deadlettered"
    );
  };

  const isDeadletteredMessage = (status: string | undefined): boolean => {
    if (!status) return false;
    const lowerStatus = status.toLowerCase();
    return lowerStatus === "deadlettered";
  };

  const isDeferredMessage = (status: string | undefined): boolean => {
    if (!status) return false;
    return status.toLowerCase() === "deferred";
  };

  // PendingHandoff entries are healthy in-flight messages. Resubmit/Skip stay
  // available so operators can override a stuck or wrong external job.
  const isPendingHandoff = (
    status: string | undefined,
    pendingSubStatus: string | null | undefined,
  ): boolean => {
    if (!status || !pendingSubStatus) return false;
    return (
      status.toLowerCase() === "pending" &&
      pendingSubStatus.toLowerCase() === "handoff"
    );
  };

  const isHandoff = isPendingHandoff(
    props.eventDetails?.resolutionStatus,
    props.eventDetails?.pendingSubStatus,
  );

  // Pending handoffs are driven by the Hero card's Complete/Fail actions, so the
  // Details-header resubmit/skip buttons are only for terminally failed events.
  const showFailedActions = isFailedMessage(props.eventDetails?.resolutionStatus);

  // The hero shows for any pending handoff, and stays visible (in its settled/
  // failed form) immediately after the operator acts.
  const heroState: HandoffState | null = heroOverride
    ? heroOverride.state
    : isHandoff
      ? "pending"
      : null;

  const confirmCompleteHandoff = async () => {
    const note = handoffNote.trim();
    setHandoffBusy(true);
    try {
      await props.completeHandoff?.(
        props.eventDetails?.endpointId!,
        props.eventDetails?.eventId!,
        props.eventDetails?.lastMessageId!,
        note || undefined,
      );
      setCompleteOpen(false);
      setHeroOverride({ state: "settled", note: note || undefined });
      addToast({
        title: "Handoff completed",
        description: "Completion published — propagating downstream.",
        variant: "success",
      });
    } catch {
      addToast({
        title: "Could not complete handoff",
        description: "The settlement request failed. Try again.",
        variant: "error",
      });
    } finally {
      setHandoffBusy(false);
    }
  };

  const confirmFailHandoff = async () => {
    const reason = failReason.trim();
    if (!reason) return;
    setHandoffBusy(true);
    try {
      await props.failHandoff?.(
        props.eventDetails?.endpointId!,
        props.eventDetails?.eventId!,
        props.eventDetails?.lastMessageId!,
        reason,
      );
      setFailOpen(false);
      setHeroOverride({ state: "failed", reason });
      addToast({
        title: "Handoff failed",
        description: "Marked as failed — will surface under Blocked downstream.",
        variant: "error",
      });
    } catch {
      addToast({
        title: "Could not fail handoff",
        description: "The settlement request failed. Try again.",
        variant: "error",
      });
    } finally {
      setHandoffBusy(false);
    }
  };

  const payloadJson = props.eventDetails?.messageContent?.eventContent?.eventJson;
  const hasPii = !!payloadJson && /\$piiMasked"\s*:\s*true/i.test(payloadJson);

  const reprocessBtn: IButtonState = { isDisabled: false, text: "Reprocess" };
  const [reprocessButton, setReprocessButton] = useState(reprocessBtn);

  const reprocessDeferredClick = async () => {
    setReprocessButton({ text: "Triggering...", isDisabled: true });
    try {
      await props.reprocessDeferred?.();
      setReprocessButton({ text: "Triggered", isDisabled: true });
    } catch {
      setReprocessButton({ text: "Failed", isDisabled: false });
    }
  };

  const skipEventClick = async () => {
    setSkipButton({ text: "Skipping...", isDisabled: true });
    try {
      await props.skipEvent(
        props.eventDetails?.eventId!,
        props.eventDetails?.lastMessageId!,
      );
      setSkipButton({ text: "Skipped", isDisabled: true });
    } catch {
      setSkipButton({ text: "Skip failed", isDisabled: false });
    }
  };

  const resubmitEventClick = async () => {
    setResubmitButton({ text: "Resubmitting...", isDisabled: true });
    try {
      await props.resubmitEvent(
        props.eventDetails?.eventId!,
        props.eventDetails?.lastMessageId!,
      );
      setResubmitButton({ text: "Resubmitted", isDisabled: true });
    } catch {
      setResubmitButton({ text: "Resubmit failed", isDisabled: false });
    }
  };

  const deleteEventClick = async () => {
    setShowDeleteConfirm(false);
    setDeleteButton({ text: "Deleting...", isDisabled: true });
    try {
      await props.deleteEvent?.();
      setDeleteButton({ text: "Deleted", isDisabled: true });
    } catch {
      setDeleteButton({ text: "Delete failed", isDisabled: false });
    }
  };

  const resubmitEventWithChangesClick = async () => {
    onClose();
    setResubmitWithChangesButton({ text: "Resubmitting...", isDisabled: true });
    const body: api.ResubmitWithChanges = api.ResubmitWithChanges.fromJS({
      eventTypeId: eventTypeIdValue,
      eventContent: textAreaValue,
    });
    try {
      await props.resubmitEventWithChanges(
        props.eventDetails?.eventId!,
        props.eventDetails?.lastMessageId!,
        body,
      );
      setResubmitWithChangesButton({ text: "Resubmitted", isDisabled: true });
    } catch {
      setResubmitWithChangesButton({
        text: "Resubmit failed",
        isDisabled: false,
      });
    }
  };

  return (
    <div className="w-full">
      {heroState && (
        <div className="mb-5 overflow-hidden rounded-nb-lg border border-border">
          <HandoffHero
            state={heroState}
            reason={props.eventDetails?.handoffReason}
            externalJobId={props.eventDetails?.externalJobId}
            expectedBy={props.eventDetails?.expectedBy}
            settledNote={heroOverride?.note}
            failedReason={heroOverride?.reason}
            resolvedBy={operatorName}
            busy={handoffBusy}
            onComplete={() => {
              setHandoffNote("");
              setCompleteOpen(true);
            }}
            onFail={() => {
              setFailReason("");
              setFailOpen(true);
            }}
          />
        </div>
      )}
      <h4 className="text-lg font-semibold flex items-center gap-4">
        Details
        {showFailedActions && (
          <div className="flex gap-2">
            <Button
              size="xs"
              colorScheme="blue"
              disabled={resubmitButton.isDisabled}
              onClick={resubmitEventClick}
            >
              {resubmitButton.text}
            </Button>
            <Button
              size="xs"
              colorScheme="green"
              disabled={resubmitWithChangesButton.isDisabled}
              onClick={onOpen}
            >
              {resubmitWithChangesButton.text}
            </Button>
            <Button
              size="xs"
              colorScheme="red"
              variant="outline"
              disabled={skipButton.isDisabled}
              onClick={skipEventClick}
            >
              {skipButton.text}
            </Button>
            {props.deleteEvent && isFailedMessage(props.eventDetails?.resolutionStatus) && (
              <Button
                size="xs"
                colorScheme="red"
                disabled={deleteButton.isDisabled}
                onClick={() => setShowDeleteConfirm(true)}
              >
                {deleteButton.text}
              </Button>
            )}
          </div>
        )}
        {isDeferredMessage(props.eventDetails?.resolutionStatus) && props.reprocessDeferred && (
          <Button
            size="xs"
            colorScheme="blue"
            disabled={reprocessButton.isDisabled}
            onClick={reprocessDeferredClick}
          >
            {reprocessButton.text}
          </Button>
        )}
      </h4>
      <br />
      {/* Timing bar — queue · processing · ack as a stacked horizontal bar.
          Operators glance once instead of scanning three KV rows (rec §09). */}
      {(props.eventDetails?.queueTimeMs !== undefined ||
        props.eventDetails?.processingTimeMs !== undefined) && (
        <TimingBar
          className="mb-4"
          segments={[
            {
              label: "Queue",
              display: formatDurationMs(props.eventDetails?.queueTimeMs),
              weight: Math.max(props.eventDetails?.queueTimeMs ?? 0, 0),
              colorClass: "bg-[#BFD8F2]",
            },
            {
              label: "Processing",
              display: formatDurationMs(
                props.eventDetails?.processingTimeMs,
              ),
              weight: Math.max(props.eventDetails?.processingTimeMs ?? 0, 0),
              colorClass: "bg-status-warning",
            },
            {
              label: "Ack",
              display: "< 1 ms",
              weight: 0.05 *
                ((props.eventDetails?.queueTimeMs ?? 0) +
                  (props.eventDetails?.processingTimeMs ?? 0) || 1),
              colorClass: "bg-status-success",
            },
          ]}
          total={
            <span>
              {formatDurationMs(
                (props.eventDetails?.queueTimeMs ?? 0) +
                  (props.eventDetails?.processingTimeMs ?? 0),
              )}{" "}
              ·{" "}
              <span
                className={
                  isFailedMessage(props.eventDetails?.resolutionStatus)
                    ? "text-status-danger"
                    : "text-status-success"
                }
              >
                {props.eventDetails?.resolutionStatus}
              </span>
            </span>
          }
          trailing={
            (props.eventDetails?.processingTimeMs ?? 0) >= SLOW_PROCESSING_MS
              ? `Above ${SLOW_PROCESSING_MS} ms — check downstream sink`
              : undefined
          }
        />
      )}

      <PropertyList>
        <PropertySection title="Identifiers">
          <PropertyRow
            label="EventId"
            value={props.eventDetails?.eventId}
            mono
          />
          <PropertyRow
            label="SessionId"
            value={props.eventDetails?.sessionId}
            mono
          />
          <PropertyRow
            label="MessageId"
            value={props.eventDetails?.lastMessageId}
            mono
          />
          {props.eventDetails?.originatingMessageId && (
            <PropertyRow
              label="OriginatingMessageId"
              value={props.eventDetails.originatingMessageId}
              mono
            />
          )}
          {/* Spec 006: render only when the event is deferred AND a blocker
              GUID was parsed AND we have an endpointId to build the link with.
              All three are required — endpointId is part of the route, and a
              row without a valid link would mislead operators. */}
          {props.eventDetails?.resolutionStatus?.toLowerCase() === "deferred" &&
            props.blockedByEventId &&
            props.eventDetails?.endpointId && (
              <PropertyRow
                label="Blocked by"
                value={
                  <Link
                    to={`/Message/Index/${props.eventDetails.endpointId}/${props.blockedByEventId}/0`}
                    className="text-status-info font-semibold no-underline hover:underline"
                  >
                    {props.blockedByEventId}
                  </Link>
                }
                mono
              />
            )}
        </PropertySection>

        <PropertySection title="Details">
          <PropertyRow
            label="EventTypeId"
            value={
              props.eventDetails?.eventTypeId && (
                <Link
                  to={`/EventTypes/Details/${props.eventDetails.eventTypeId}`}
                  className="text-status-info font-semibold no-underline hover:underline"
                >
                  {props.eventDetails.eventTypeId}
                </Link>
              )
            }
          />
          <PropertyRow
            label="Source Endpoint"
            value={
              (props.eventDetails?.originatingFrom ||
                props.eventDetails?.from) && (
                <Link
                  to={`/Endpoints/Details/${props.eventDetails?.originatingFrom || props.eventDetails?.from}`}
                  className="text-status-info font-semibold no-underline hover:underline"
                >
                  {props.eventDetails?.originatingFrom ||
                    props.eventDetails?.from}
                </Link>
              )
            }
          />
          <PropertyRow
            label="Status"
            value={
              <span className="inline-flex items-center gap-2">
                <Badge
                  variant={statusToBadgeVariant(
                    props.eventDetails?.resolutionStatus,
                  )}
                  size="sm"
                >
                  {props.eventDetails?.resolutionStatus ?? "—"}
                </Badge>
                {isPendingHandoff(
                  props.eventDetails?.resolutionStatus,
                  props.eventDetails?.pendingSubStatus,
                ) && (
                  <Badge variant="info" size="sm">
                    Awaiting external
                  </Badge>
                )}
              </span>
            }
          />
          <PropertyRow
            label="Enqueued"
            value={formatMoment(props.eventDetails?.enqueuedTimeUtc)}
            mono
          />
          <PropertyRow
            label="Queue Time"
            value={formatDurationMs(props.eventDetails?.queueTimeMs)}
            mono
          />
          <PropertyRow
            label="Processing Time"
            value={
              (props.eventDetails?.processingTimeMs ?? 0) >=
              SLOW_PROCESSING_MS ? (
                <Badge variant="warning" size="sm">
                  {formatDurationMs(props.eventDetails?.processingTimeMs)}
                </Badge>
              ) : (
                <span className="font-mono text-[12px] tabular-nums">
                  {formatDurationMs(props.eventDetails?.processingTimeMs)}
                </span>
              )
            }
          />
        </PropertySection>

        {(props.eventDetails?.handoffReason ||
          props.eventDetails?.externalJobId ||
          props.eventDetails?.expectedBy) && (
          <PropertySection title="Handoff details">
            {props.eventDetails?.handoffReason && (
              <PropertyRow
                label="Reason"
                value={props.eventDetails.handoffReason}
              />
            )}
            {props.eventDetails?.externalJobId && (
              <PropertyRow
                label="External Job Id"
                value={props.eventDetails.externalJobId}
                mono
              />
            )}
            {props.eventDetails?.expectedBy && (
              <PropertyRow
                label="Expected By"
                value={formatMoment(props.eventDetails.expectedBy)}
                mono
              />
            )}
          </PropertySection>
        )}
      </PropertyList>
      <br />
      {isFailedMessage(props.eventDetails?.resolutionStatus) && (
        <>
          <h4 className="text-lg font-semibold mt-4">Error</h4>
          <br />
          {props.eventDetails?.messageContent?.errorContent && (
            !isDeadletteredMessage(props.eventDetails?.resolutionStatus) ? (
              <>
                <div className="bg-red-100 border border-red-400 text-red-800 dark:bg-red-950/40 dark:border-red-900/60 dark:text-red-200 p-4 rounded text-sm">
                  {props.eventDetails?.messageContent?.errorContent?.errorText}
                </div>
                <table className="text-sm">
                  <tbody>
                    <tr className="hover:bg-accent">
                      <td className="py-2 pr-4">
                        <b>Exception</b>
                      </td>
                      <td className="py-2">
                        {
                          props.eventDetails?.messageContent?.errorContent
                            ?.exceptionStackTrace
                        }
                      </td>
                    </tr>
                  </tbody>
                </table>
              </>
            ) : (
              <table className="text-sm">
                <tbody>
                  <tr className="hover:bg-accent">
                    <td className="py-2 pr-4">
                      <b>DeadLetter Reason</b>
                    </td>
                    <td className="py-2">
                      {props.eventDetails?.deadLetterReason}
                    </td>
                  </tr>
                  <tr className="hover:bg-accent">
                    <td className="py-2 pr-4">
                      <b>DeadLetter Error Description</b>
                    </td>
                    <td className="py-2">
                      {props.eventDetails?.deadLetterErrorDescription}
                    </td>
                  </tr>
                </tbody>
              </table>
            )
          )}
          <br />
        </>
      )}

      {payloadJson && (
        <div className="mt-4">
          {/* Masked payloads carry "$piiMasked": true. Keep the (already
              server-hashed) values blurred by default so PII tokens aren't
              shoulder-surfed; the operator reveals them on demand. */}
          <div
            className={
              hasPii && !piiRevealed
                ? "[&_pre]:blur-[5px] [&_pre]:select-none transition-[filter] duration-150"
                : "transition-[filter] duration-150"
            }
          >
            <CodeBlock
              title="Payload"
              subtitle="application/json"
              actions={
                hasPii ? (
                  <button
                    type="button"
                    onClick={() => setPiiRevealed((v) => !v)}
                    className="font-mono text-[11px] font-semibold text-primary-600 dark:text-primary-400 border border-border rounded px-2 py-1 hover:bg-primary-tint dark:hover:bg-primary-900/40"
                  >
                    {piiRevealed ? "Hide PII" : "Reveal PII"}
                  </button>
                ) : undefined
              }
              linkifyGuid={(_guid) =>
                // Clicking a GUID in the payload jumps to the Messages search
                // pre-filtered to that ID — IDs become first-class navigation
                // bridges between pages (design rec §09 code).
                `/Messages?eventId=${_guid}`
              }
            >
              {safeFormatJson(payloadJson)}
            </CodeBlock>
          </div>
        </div>
      )}

      <Modal isOpen={isOpen} onClose={onClose} size="2xl">
        <ModalHeader onClose={onClose}>Resubmit with changes</ModalHeader>
        <ModalBody>
          <div className="mb-4">
            <label className="block text-sm font-medium text-foreground mb-2">
              Original event:
            </label>
            <pre className="bg-muted p-4 rounded text-sm overflow-x-auto max-h-96">
              {props.eventDetails?.messageContent?.eventContent?.eventJson
                ? safeFormatJson(
                    props.eventDetails.messageContent.eventContent.eventJson,
                  )
                : ""}
            </pre>
          </div>
          <div className="mb-4">
            <label className="block text-sm font-medium text-foreground mb-2">
              Event type:
            </label>
            <Select
              onChange={handleEventTypeIdChange}
              defaultValue={props.eventDetails?.eventTypeId}
            >
              {props.eventTypes.map((et) => (
                <option key={et.id} value={et.id}>
                  {et.id}
                </option>
              ))}
            </Select>
          </div>
          <div>
            <label className="block text-sm font-medium text-foreground mb-2">
              Modified event:
            </label>
            <Textarea
              value={textAreaValue}
              onChange={handleInputChange}
              className="min-h-[300px]"
            />
          </div>
        </ModalBody>
        <ModalFooter>
          <Button variant="outline" onClick={onClose}>
            Close
          </Button>
          <Button onClick={resubmitEventWithChangesClick}>Resubmit</Button>
        </ModalFooter>
      </Modal>

      {/* Comment Section */}
      <CommentSection eventId={props.eventDetails?.eventId} onCommentAdded={props.onCommentAdded} />

      <Modal isOpen={showDeleteConfirm} onClose={() => setShowDeleteConfirm(false)}>
        <ModalHeader>Delete Event</ModalHeader>
        <ModalBody>
          <p className="text-sm">
            This will permanently delete the event from storage.{" "}
            <span className="font-semibold text-red-600 dark:text-red-400">This action cannot be undone.</span>
          </p>
        </ModalBody>
        <ModalFooter>
          <Button variant="outline" onClick={() => setShowDeleteConfirm(false)}>
            Cancel
          </Button>
          <Button colorScheme="red" onClick={deleteEventClick}>
            Delete
          </Button>
        </ModalFooter>
      </Modal>

      {/* Complete handoff */}
      <Modal isOpen={completeOpen} onClose={() => setCompleteOpen(false)}>
        <ModalHeader onClose={() => setCompleteOpen(false)}>
          Complete handoff?
        </ModalHeader>
        <ModalBody>
          <p className="text-sm text-muted-foreground">
            Mark this event as successfully handed off. A{" "}
            <code className="font-mono text-xs bg-muted px-1.5 py-0.5 rounded">
              HandoffCompletedRequest
            </code>{" "}
            is published to the subscriber, moving it out of pending so it
            propagates downstream.
          </p>
          <div className="mt-3 rounded-nb-md border border-border bg-canvas dark:bg-muted/40 p-3 flex flex-col gap-1.5">
            <div className="flex justify-between font-mono text-[11.5px]">
              <span className="text-muted-foreground">Event</span>
              <b className="font-semibold text-foreground">
                {props.eventDetails?.eventTypeId ?? "—"}
              </b>
            </div>
            {props.eventDetails?.externalJobId && (
              <div className="flex justify-between font-mono text-[11.5px]">
                <span className="text-muted-foreground">External job</span>
                <b className="font-semibold text-foreground">
                  {props.eventDetails.externalJobId}
                </b>
              </div>
            )}
            <div className="flex justify-between font-mono text-[11.5px]">
              <span className="text-muted-foreground">Endpoint</span>
              <b className="font-semibold text-foreground">
                {props.eventDetails?.endpointId ?? "—"}
              </b>
            </div>
          </div>
          <label className="block mt-3.5 mb-1.5 text-xs font-semibold text-foreground">
            Operator note{" "}
            <span className="font-normal text-muted-foreground">
              (optional, written to audit log)
            </span>
          </label>
          <Textarea
            value={handoffNote}
            onChange={(e) => setHandoffNote(e.target.value)}
            placeholder="e.g. Verified batch finished cleanly in the external console."
            rows={3}
          />
        </ModalBody>
        <ModalFooter>
          <Button variant="outline" onClick={() => setCompleteOpen(false)}>
            Cancel
          </Button>
          <Button
            colorScheme="blue"
            isLoading={handoffBusy}
            disabled={handoffBusy}
            onClick={confirmCompleteHandoff}
          >
            Complete handoff
          </Button>
        </ModalFooter>
      </Modal>

      {/* Fail handoff */}
      <Modal isOpen={failOpen} onClose={() => setFailOpen(false)}>
        <ModalHeader onClose={() => setFailOpen(false)}>
          Fail this handoff?
        </ModalHeader>
        <ModalBody>
          <p className="text-sm text-muted-foreground">
            Mark the event as terminally failed. A{" "}
            <code className="font-mono text-xs bg-muted px-1.5 py-0.5 rounded">
              HandoffFailedRequest
            </code>{" "}
            is published; the event surfaces under the{" "}
            <b className="font-semibold text-foreground">Blocked</b> tab on
            dependent endpoints and triggers any failure alerts.
          </p>
          <div className="mt-3 rounded-nb-md border border-border bg-canvas dark:bg-muted/40 p-3 flex flex-col gap-1.5">
            <div className="flex justify-between font-mono text-[11.5px]">
              <span className="text-muted-foreground">Event</span>
              <b className="font-semibold text-foreground">
                {props.eventDetails?.eventTypeId ?? "—"}
              </b>
            </div>
            <div className="flex justify-between font-mono text-[11.5px]">
              <span className="text-muted-foreground">Reversible</span>
              <b className="font-semibold text-foreground">
                No — re-publish needed
              </b>
            </div>
          </div>
          <label className="block mt-3.5 mb-1.5 text-xs font-semibold text-foreground">
            Failure reason{" "}
            <span className="font-semibold text-status-danger">required</span>
          </label>
          <Textarea
            value={failReason}
            onChange={(e) => setFailReason(e.target.value)}
            placeholder="e.g. External system reported permanent failure; batch was rejected."
            rows={3}
          />
        </ModalBody>
        <ModalFooter>
          <Button variant="outline" onClick={() => setFailOpen(false)}>
            Cancel
          </Button>
          <Button
            colorScheme="red"
            isLoading={handoffBusy}
            disabled={handoffBusy || !failReason.trim()}
            onClick={confirmFailHandoff}
          >
            Fail handoff
          </Button>
        </ModalFooter>
      </Modal>
    </div>
  );
}

function CommentSection({ eventId, onCommentAdded }: { eventId?: string; onCommentAdded?: () => void }) {
  const [comment, setComment] = useState("");
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);

  const submit = async () => {
    if (!comment.trim() || !eventId) return;
    setSaving(true);
    setSaved(false);
    try {
      const client = new api.Client(api.CookieAuth());
      let userName = "Unknown";
      try {
        const me = await client.getMe();
        if (me?.name) userName = me.name;
      } catch { /* fallback */ }

      const audit = new api.MessageAudit();
      audit.auditorName = userName;
      audit.auditTimestamp = new Date() as any;
      audit.auditType = api.MessageAuditAuditType.Comment;
      audit.comment = comment.trim();

      await client.postMessageAudit(eventId, audit);
      setComment("");
      setSaved(true);
      onCommentAdded?.();
    } catch {
      // error
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="mt-6 border-t pt-4">
      <h5 className="text-base font-semibold mb-2">Add Comment</h5>
      <Textarea
        value={comment}
        onChange={(e) => { setComment(e.target.value); setSaved(false); }}
        placeholder="Enter a comment..."
        rows={3}
      />
      <div className="flex items-center gap-2 mt-2">
        <Button size="sm" colorScheme="primary" onClick={submit} disabled={saving || !comment.trim()}>
          {saving ? "Saving..." : "Save"}
        </Button>
        {saved && <span className="text-sm text-green-600 dark:text-green-400">Saved</span>}
      </div>
    </div>
  );
}
