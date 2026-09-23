import { useEffect, useRef, useState } from "react";
import * as api from "api-client";
import { Button } from "components/ui/button";
import {
  Modal,
  ModalBody,
  ModalFooter,
  ModalHeader,
} from "components/ui/modal";
import { Textarea } from "components/ui/textarea";
import { useToast } from "components/ui/toast";

export default function DeferredRecovery(props: {
  endpointId: string;
  eventId: string;
  onResolved: () => Promise<void>;
}) {
  const [inspection, setInspection] = useState<api.DeferredInspection>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [confirm, setConfirm] = useState(false);
  const [reason, setReason] = useState("");
  const generation = useRef(0);
  const { addToast } = useToast();
  useEffect(() => {
    generation.current++;
    setInspection(undefined);
    setBusy(false);
    setError("");
    setConfirm(false);
    setReason("");
    return () => {
      generation.current++;
    };
  }, [props.endpointId, props.eventId]);

  const inspect = async () => {
    const ticket = ++generation.current;
    setBusy(true);
    setError("");
    setInspection(undefined);
    try {
      const result = await new api.Client(
        api.CookieAuth(),
      ).getDeferredInspection(props.endpointId, props.eventId);
      if (ticket === generation.current) setInspection(result);
    } catch {
      if (ticket === generation.current)
        setError(
          "Could not inspect this event. Check connectivity and permissions, then retry.",
        );
    } finally {
      if (ticket === generation.current) setBusy(false);
    }
  };

  const skip = async () => {
    if (!inspection?.canSkip || !reason.trim() || !inspection.rowVersion)
      return;
    const ticket = ++generation.current;
    setBusy(true);
    setError("");
    let result: api.DeferredSkipResult;
    try {
      result = await new api.Client(api.CookieAuth()).postSkipDeferredTracking(
        props.endpointId,
        props.eventId,
        new api.DeferredSkipRequest({
          rowVersion: inspection.rowVersion,
          reason: reason.trim(),
        }),
      );
    } catch {
      if (ticket === generation.current) {
        setInspection(undefined);
        setConfirm(false);
        setBusy(false);
        setError(
          "Skip was not confirmed. The record or broker state may have changed, or access was denied. Check again before retrying.",
        );
      }
      return;
    }
    if (ticket !== generation.current) return;
    setConfirm(false);
    setInspection(undefined);
    setBusy(false);
    addToast({
      title: result.auditRecorded
        ? "Tracking record skipped"
        : "Tracking record skipped; audit could not be saved",
      description: result.auditRecorded
        ? "History was preserved. No message was published or removed from the broker."
        : "The skip succeeded, but its audit entry is missing. Contact an administrator.",
      variant: result.auditRecorded ? "success" : "error",
    });
    try {
      await props.onResolved();
    } catch {
      if (ticket === generation.current)
        setError(
          "The record was skipped, but refresh failed. Reload the page.",
        );
    }
  };

  return (
    <section
      className="my-4 rounded-nb-lg border border-border bg-surface p-4"
      aria-label="Deferred message recovery"
    >
      <div className="flex items-center justify-between gap-3">
        <div>
          <h4 className="font-semibold">Deferred message recovery</h4>
          <p className="text-sm text-muted-foreground">
            Check the broker and recorded processing history when Reprocess has
            no effect.
          </p>
        </div>
        <Button size="sm" variant="outline" disabled={busy} onClick={inspect}>
          {busy
            ? "Checking…"
            : inspection
              ? "Check again"
              : "Check deferred message"}
        </Button>
      </div>
      {error && (
        <p role="alert" className="mt-3 text-sm text-red-600">
          {error}
        </p>
      )}
      {inspection && (
        <div className="mt-4 space-y-3 text-sm">
          <p>
            <strong>Recorded outcome: {inspection.historyOutcome}</strong>
            <br />
            {inspection.historyDetail}
          </p>
          {inspection.terminalMessageId && (
            <p className="font-mono text-xs">
              Response: {inspection.terminalMessageId}
              {inspection.terminalTime && (
                <> · {inspection.terminalTime.format("YYYY-MM-DD HH:mm:ss")}</>
              )}
            </p>
          )}
          <table className="w-full text-left">
            <caption className="sr-only">Broker inspection</caption>
            <thead>
              <tr>
                <th>Subscription</th>
                <th>Presence</th>
                <th>Details</th>
              </tr>
            </thead>
            <tbody>
              {inspection.brokerChecks?.map((check) => (
                <tr key={check.location} className="border-t border-border">
                  <td className="py-2 font-mono text-xs">{check.location}</td>
                  <td>
                    {check.status === api.DeferredBrokerCheckStatus.NotFound
                      ? "Not found"
                      : check.status}
                  </td>
                  <td>
                    {check.detail} ({check.scanned ?? 0} scanned)
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <p>{inspection.skipDetail}</p>
          <p className="text-muted-foreground">
            This is a point-in-time check. Scheduled messages and future replays
            may still arrive. Skipping changes only this tracking record; it
            does not cancel in-flight processing or unblock the session.
          </p>
          {inspection.canSkip && (
            <Button
              size="sm"
              colorScheme="red"
              variant="outline"
              onClick={() => setConfirm(true)}
            >
              Skip tracking record
            </Button>
          )}
        </div>
      )}
      <Modal
        isOpen={confirm}
        onClose={() => {
          if (!busy) setConfirm(false);
        }}
      >
        <ModalHeader>Skip deferred tracking record</ModalHeader>
        <ModalBody>
          <p className="mb-3 text-sm">
            Mark this event Skipped and remove it from the Deferred list. Its
            history and your reason remain in the audit trail. This does not
            prove successful processing or cancel any in-flight work. The broker
            will be checked again.
          </p>
          <label htmlFor="deferred-skip-reason" className="text-sm font-medium">
            Reason
          </label>
          <Textarea
            id="deferred-skip-reason"
            value={reason}
            maxLength={1000}
            disabled={busy}
            onChange={(e) => setReason(e.target.value)}
          />
        </ModalBody>
        <ModalFooter>
          <Button
            variant="outline"
            disabled={busy}
            onClick={() => setConfirm(false)}
          >
            Cancel
          </Button>
          <Button
            colorScheme="red"
            disabled={busy || !reason.trim()}
            onClick={skip}
          >
            {busy ? "Skipping…" : "Confirm skip"}
          </Button>
        </ModalFooter>
      </Modal>
    </section>
  );
}
