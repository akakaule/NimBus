import { useState } from "react";
import * as api from "api-client";
import { Badge, type BadgeVariant } from "components/ui/badge";
import { Button } from "components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "components/ui/card";
import { Combobox } from "components/ui/combobox";
import { Input } from "components/ui/input";
import { StatTile } from "components/ui/stat-tile";
import TruncatedGuid from "components/common/truncated-guid";
import ConfirmDestructiveAction from "./confirm-destructive-action";
import OperationProgress from "./operation-progress";

interface EndpointOption {
  value: string;
  label: string;
}

/**
 * Spec 032. A row that a redelivered request copy reopened after the endpoint had already
 * answered reads Pending forever: the Resolver's outcome is stored, the row just does not
 * reflect it. Preview classifies every Pending row from its stored history; Repair re-applies
 * the stored ResolutionResponse to the rows the rule calls Repairable and nothing else.
 */
const VERDICT_TONE: Record<string, BadgeVariant> = {
  Repairable: "completed",
  HistoryMissing: "warning",
  NoTerminal: "pending",
  LatestTerminalIsError: "warning",
  LatestTerminalIsSkip: "warning",
  LatestTerminalIsDeadLettered: "warning",
  ResponseNotBeforeRow: "pending",
  LaterControlMessage: "warning",
  LaterRequestCopy: "pending",
};

/** The card sends UTC, so the local datetime-local value is converted, never pasted through. */
function toIsoUtc(localValue: string): Date | undefined {
  if (!localValue) return undefined;
  const parsed = new Date(localValue);
  return Number.isNaN(parsed.getTime()) ? undefined : parsed;
}

function defaultCutoff(): string {
  const anHourAgo = new Date(Date.now() - 60 * 60 * 1000);
  const offsetMs = anHourAgo.getTimezoneOffset() * 60 * 1000;
  return new Date(anHourAgo.getTime() - offsetMs).toISOString().slice(0, 16);
}

function formatTime(value: unknown): string {
  if (!value) return "—";
  const asAny = value as { toISOString?: () => string };
  return typeof asAny.toISOString === "function"
    ? asAny.toISOString().replace("T", " ").slice(0, 19)
    : String(value);
}

function toCsv(rows: api.StalePendingRow[]): string {
  const header = [
    "eventId",
    "sessionId",
    "eventTypeId",
    "rowMessageType",
    "staleMessageId",
    "rowEnqueuedTimeUtc",
    "rowUpdatedAt",
    "verdict",
    "responseMessageId",
    "responseEnqueuedTimeUtc",
    "detail",
  ];
  const escape = (value: unknown) => `"${String(value ?? "").replace(/"/g, '""')}"`;
  const lines = rows.map((row) =>
    [
      row.eventId,
      row.sessionId,
      row.eventTypeId,
      row.rowMessageType,
      row.staleMessageId,
      formatTime(row.rowEnqueuedTimeUtc),
      formatTime(row.rowUpdatedAt),
      row.verdict,
      row.responseMessageId,
      formatTime(row.responseEnqueuedTimeUtc),
      row.detail,
    ]
      .map(escape)
      .join(","),
  );
  return [header.join(","), ...lines].join("\n");
}

export function StalePendingReconcileCard({
  endpoints,
}: {
  endpoints: EndpointOption[];
}) {
  const [selected, setSelected] = useState<string[]>([]);
  const [cutoff, setCutoff] = useState(defaultCutoff);
  const [note, setNote] = useState("");
  const [preview, setPreview] = useState<api.StalePendingPreview | null>(null);
  const [result, setResult] = useState<api.StalePendingReconcileResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [executing, setExecuting] = useState(false);
  const [showConfirm, setShowConfirm] = useState(false);

  const endpointId = selected[0] ?? "";
  const rows = preview?.rows ?? [];
  const repairable = preview?.repairable ?? 0;

  function buildRequest(): api.StalePendingReconcileRequest {
    const request = new api.StalePendingReconcileRequest();
    const before = toIsoUtc(cutoff);
    if (before) request.enqueuedBefore = before as never;
    if (note) request.note = note;
    return request;
  }

  function describeError(caught: unknown): string {
    const swagger = caught as { status?: number; message?: string };
    if (swagger?.status === 400) {
      return "The cut-off must be at least 15 minutes in the past.";
    }
    if (swagger?.status === 403) {
      return "Reconciling stale Pending rows requires the site Owner role.";
    }
    return swagger?.message ?? "The request failed.";
  }

  async function runPreview() {
    if (!endpointId) return;
    setLoading(true);
    setError(null);
    setResult(null);
    try {
      const client = new api.Client(api.CookieAuth());
      setPreview(await client.postAdminStalePendingPreview(endpointId, buildRequest()));
    } catch (caught) {
      setPreview(null);
      setError(describeError(caught));
    } finally {
      setLoading(false);
    }
  }

  async function runRepair() {
    if (!endpointId) return;
    setShowConfirm(false);
    setExecuting(true);
    setError(null);
    try {
      const client = new api.Client(api.CookieAuth());
      setResult(await client.postAdminStalePendingReconcile(endpointId, buildRequest()));
      // The repaired rows are no longer Pending: re-read so the table matches the store.
      setPreview(await client.postAdminStalePendingPreview(endpointId, buildRequest()));
    } catch (caught) {
      setError(describeError(caught));
    } finally {
      setExecuting(false);
    }
  }

  function downloadCsv() {
    const blob = new Blob([toCsv(rows)], { type: "text/csv;charset=utf-8" });
    const objectUrl = URL.createObjectURL(blob);
    try {
      // The anchor has to be in the document for a programmatic click to start a
      // download in Firefox — same pattern as asyncapi-export.tsx.
      const anchor = document.createElement("a");
      anchor.href = objectUrl;
      anchor.download = `stale-pending-${endpointId}.csv`;
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
    } finally {
      URL.revokeObjectURL(objectUrl);
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Reconcile Stale Pending</CardTitle>
        <CardDescription>
          Re-apply the outcome the Resolver already stored to rows a redelivered request copy
          left Pending. Never resubmits, never skips, never invents a status.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <div className="space-y-4">
          <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
            <div>
              <label className="block text-xs font-medium text-muted-foreground mb-1">
                Endpoint
              </label>
              <Combobox
                options={endpoints}
                value={selected}
                onChange={(v) => {
                  setSelected(v);
                  setPreview(null);
                  setResult(null);
                  setError(null);
                }}
                placeholder="Select endpoint..."
                multiple={false}
              />
            </div>
            <div>
              <label className="block text-xs font-medium text-muted-foreground mb-1">
                Enqueued before (local, sent as UTC)
              </label>
              <Input
                type="datetime-local"
                value={cutoff}
                onChange={(e) => setCutoff(e.target.value)}
              />
            </div>
            <div>
              <label className="block text-xs font-medium text-muted-foreground mb-1">
                Note (audited)
              </label>
              <Input
                value={note}
                onChange={(e) => setNote(e.target.value)}
                placeholder="Incident or ticket reference"
              />
            </div>
          </div>

          <Button
            onClick={runPreview}
            disabled={!endpointId || loading}
            isLoading={loading}
            variant="outline"
          >
            Preview
          </Button>

          {error && (
            <p role="alert" className="text-sm text-status-danger">
              {error}
            </p>
          )}

          {preview && (
            <div className="space-y-4">
              <div className="grid grid-cols-3 gap-3">
                <StatTile label="Candidates" value={preview.candidates ?? 0} tone="muted" />
                <StatTile label="Repairable" value={repairable} />
                <StatTile
                  label="Operator decision"
                  value={(preview.candidates ?? 0) - repairable}
                  tone="warning"
                />
              </div>

              {preview.truncated && (
                <p className="text-xs text-status-warning">
                  The preview stopped at its row cap; repair what is listed and preview again.
                </p>
              )}

              {rows.length === 0 ? (
                <p className="text-sm text-muted-foreground">
                  No Pending rows before that cut-off.
                </p>
              ) : (
                <>
                  <table className="w-full text-sm">
                    <thead>
                      <tr className="text-left text-xs uppercase text-muted-foreground">
                        <th className="py-1 pr-3">Verdict</th>
                        <th className="py-1 pr-3">Event</th>
                        <th className="py-1 pr-3">Stale message</th>
                        <th className="py-1 pr-3">Row enqueued</th>
                        <th className="py-1 pr-3">Response</th>
                        <th className="py-1">Why</th>
                      </tr>
                    </thead>
                    <tbody>
                      {rows.map((row) => (
                        <tr key={`${row.eventId}-${row.sessionId ?? ""}`} className="group border-t border-border">
                          <td className="py-1.5 pr-3 align-top">
                            <Badge variant={VERDICT_TONE[row.verdict ?? ""] ?? "default"} size="sm">
                              {row.verdict}
                            </Badge>
                          </td>
                          <td className="py-1.5 pr-3 align-top">
                            <TruncatedGuid guid={row.eventId} />
                          </td>
                          <td className="py-1.5 pr-3 align-top">
                            <TruncatedGuid guid={row.staleMessageId} />
                          </td>
                          <td className="py-1.5 pr-3 align-top whitespace-nowrap font-mono text-[11.5px]">
                            {formatTime(row.rowEnqueuedTimeUtc)}
                          </td>
                          <td className="py-1.5 pr-3 align-top">
                            <TruncatedGuid guid={row.responseMessageId} />
                          </td>
                          <td className="py-1.5 align-top font-mono text-[11.5px] text-muted-foreground">
                            {row.detail}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>

                  <div className="flex gap-3">
                    <Button variant="outline" size="sm" onClick={downloadCsv}>
                      Download CSV
                    </Button>
                    <Button
                      colorScheme="red"
                      size="sm"
                      disabled={repairable === 0 || executing}
                      isLoading={executing}
                      onClick={() => setShowConfirm(true)}
                    >
                      Repair {repairable} rows
                    </Button>
                  </div>
                </>
              )}
            </div>
          )}

          {result && (
            <OperationProgress
              processed={result.processed ?? 0}
              succeeded={result.succeeded ?? 0}
              failed={result.failed ?? 0}
              errors={result.errors}
              isComplete={true}
            />
          )}

          <ConfirmDestructiveAction
            isOpen={showConfirm}
            onClose={() => setShowConfirm(false)}
            onConfirm={runRepair}
            title="Reconcile Stale Pending"
            description={`This will replace ${repairable} Pending row(s) on "${endpointId}" with the Completed outcome the Resolver already stored. Every repair is audited.`}
            confirmText={endpointId}
            confirmLabel={`Repair ${repairable} rows`}
            isLoading={executing}
          />
        </div>
      </CardContent>
    </Card>
  );
}

export default StalePendingReconcileCard;
