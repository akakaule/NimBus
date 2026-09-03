import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import * as api from "api-client";
import { Button } from "components/ui/button";
import { Modal, ModalBody, ModalFooter, ModalHeader } from "components/ui/modal";
import { Radio, RadioGroup } from "components/ui/radio-group";
import { Spinner } from "components/ui/spinner";

type Selection =
  | { kind: "all" }
  | { kind: "reason"; reason: string | null };

const nf = new Intl.NumberFormat();

export default function ResolverDeadLetterDialog({
  client,
  subscriptionName,
  onClose,
  onReplayed,
}: {
  client: api.Client;
  subscriptionName: string;
  onClose: () => void;
  onReplayed: () => Promise<void>;
}) {
  const [overview, setOverview] = useState<api.DeadLetterOverview | null>(null);
  const [selection, setSelection] = useState<Selection>({ kind: "all" });
  const [loading, setLoading] = useState(true);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [feedback, setFeedback] = useState<string | null>(null);
  const [refreshWarning, setRefreshWarning] = useState<string | null>(null);
  const loadSequence = useRef(0);

  const load = useCallback(async () => {
    const sequence = ++loadSequence.current;
    try {
      const next = await client.getAdminServicebusResolverDeadletters(subscriptionName);
      if (loadSequence.current !== sequence) return;
      setOverview(next);
      setError(null);
    } catch (err: any) {
      if (loadSequence.current !== sequence) return;
      setError(err?.message ?? "Failed to inspect Resolver dead letters.");
    } finally {
      if (loadSequence.current === sequence) setLoading(false);
    }
  }, [client, subscriptionName]);

  useEffect(() => {
    void load();
    return () => {
      loadSequence.current++;
    };
  }, [load]);

  const selectedCount = useMemo(() => {
    if (!overview) return 0;
    if (selection.kind === "all") return overview.totalMessageCount ?? 0;
    return (
      overview.reasons?.find((item) => item.reason === selection.reason)?.count ?? 0
    );
  }, [overview, selection]);

  async function resubmit() {
    setSubmitting(true);
    setError(null);
    setRefreshWarning(null);
    try {
      const body = {
        scope:
          selection.kind === "all"
            ? api.DeadLetterResubmitRequestScope.All
            : api.DeadLetterResubmitRequestScope.Reason,
        ...(selection.kind === "reason" ? { reason: selection.reason } : {}),
      } as api.DeadLetterResubmitRequest;
      const result = await client.postAdminServicebusResolverDeadlettersResubmit(
        body,
        subscriptionName,
      );
      const succeeded = result.succeeded ?? 0;
      const failed = result.failed ?? 0;
      setFeedback(
        failed > 0
          ? `Resubmitted ${nf.format(succeeded)} of ${nf.format(selectedCount)} messages; ${nf.format(failed)} remain dead-lettered.`
          : `Resubmitted ${nf.format(succeeded)} of ${nf.format(selectedCount)} message(s).`,
      );

      const refreshes = await Promise.allSettled([load(), onReplayed()]);
      if (refreshes.some((refresh) => refresh.status === "rejected")) {
        setRefreshWarning("Replay succeeded, but the displayed counts could not all be refreshed.");
      }
    } catch (err: any) {
      setError(err?.message ?? "Resolver dead-letter replay failed.");
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <Modal isOpen onClose={onClose} size="lg" closeOnOverlayClick={!submitting}>
      <ModalHeader onClose={submitting ? undefined : onClose}>
        Resolver dead letters
      </ModalHeader>
      <ModalBody className="space-y-4">
        {loading && !overview ? (
          <div className="flex justify-center py-8"><Spinner /></div>
        ) : overview ? (
          <>
            <p className="text-sm text-muted-foreground m-0">
              Choose the exact dead-letter reason to replay, or replay every message
              in this operation snapshot.
            </p>
            {overview.isTruncated && (
              <div className="rounded-nb-md border border-status-warning/30 bg-status-warning-50 p-3 text-sm text-status-warning-ink">
                Counts cover the first {nf.format(overview.snapshotLimit ?? 500)}-message snapshot batch. Repeat the operation to process later messages.
              </div>
            )}
            <RadioGroup
              name="resolver-dead-letter-selection"
              value={selection.kind === "all" ? "all" : `reason:${overview.reasons?.findIndex((r) => r.reason === selection.reason)}`}
              onChange={(value) => {
                if (value === "all") setSelection({ kind: "all" });
                else {
                  const index = Number(value.slice("reason:".length));
                  setSelection({ kind: "reason", reason: overview.reasons?.[index]?.reason ?? null });
                }
              }}
              disabled={submitting}
              className="flex-col items-start"
            >
              <Radio value="all">
                {overview.isTruncated ? "All messages in this snapshot" : "All dead letters"} ({nf.format(overview.totalMessageCount ?? 0)})
              </Radio>
              {(overview.reasons ?? []).map((item, index) => (
                <Radio key={`${index}:${item.reason ?? "null"}`} value={`reason:${index}`}>
                  <span className="font-mono">{item.reason === null ? "No reason" : item.reason === "" ? "Empty reason" : item.reason}</span> ({nf.format(item.count ?? 0)})
                </Radio>
              ))}
            </RadioGroup>
          </>
        ) : null}
        {feedback && <div className="text-sm text-status-success-ink">{feedback}</div>}
        {refreshWarning && <div className="text-sm text-status-warning-ink">{refreshWarning}</div>}
        {error && <div className="text-sm text-status-danger-ink">{error}</div>}
      </ModalBody>
      <ModalFooter>
        <Button variant="ghost" onClick={onClose} disabled={submitting}>Close</Button>
        <Button onClick={resubmit} isLoading={submitting} disabled={!overview || selectedCount === 0}>
          Resubmit {nf.format(selectedCount)} {selectedCount === 1 ? "message" : "messages"}
        </Button>
      </ModalFooter>
    </Modal>
  );
}
