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
  onCommentAdded?: () => void;
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

  useEffect(() => {
    setTextAreaValue(
      props.eventDetails?.messageContent?.eventContent?.eventJson,
    );
    setShowErrorDetails(false);
  }, [props.eventDetails]);

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

  const showOperatorActions =
    isFailedMessage(props.eventDetails?.resolutionStatus) ||
    isPendingHandoff(
      props.eventDetails?.resolutionStatus,
      props.eventDetails?.pendingSubStatus,
    );

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
      <h4 className="text-lg font-semibold flex items-center gap-4">
        Details
        {showOperatorActions && (
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
            label="Enqueued (UTC)"
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

      {props.eventDetails?.messageContent?.eventContent?.eventJson && (
        <div className="mt-4">
          <CodeBlock
            title="Payload"
            subtitle="application/json"
            linkifyGuid={(_guid) =>
              // Clicking a GUID in the payload jumps to the Messages search
              // pre-filtered to that ID — IDs become first-class navigation
              // bridges between pages (design rec §09 code).
              `/Messages?eventId=${_guid}`
            }
          >
            {safeFormatJson(
              props.eventDetails.messageContent.eventContent.eventJson,
            )}
          </CodeBlock>
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
