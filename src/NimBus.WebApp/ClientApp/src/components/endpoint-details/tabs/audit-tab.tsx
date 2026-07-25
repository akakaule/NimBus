import { useCallback, useEffect, useRef, useState } from "react";
import * as api from "api-client";
import { formatMoment } from "functions/endpoint.functions";
import DataTable, { ITableHeadCell, ITableRow } from "components/data-table";
import TruncatedGuid from "components/common/truncated-guid";
import { Badge } from "components/ui/badge";
import { Button } from "components/ui/button";
import { notifyInfo } from "functions/notifications.functions";

interface IAuditTabProps {
  endpointId: string;
}

const PAGE_SIZE = 50;

const headCells: ITableHeadCell[] = [
  { id: "createdAt", label: "Timestamp", numeric: false },
  { id: "auditType", label: "Action", numeric: false },
  { id: "auditorName", label: "Auditor", numeric: false },
  { id: "eventId", label: "Event Id", numeric: false },
  {
    id: "data",
    label: "Data",
    numeric: false,
    info: "Structured context recorded with the action (click to copy)",
  },
  { id: "accessDenied", label: "Access Denied", numeric: false },
];

// Endpoint-scoped audit trail (the "Audit" tab): every operator action on this
// endpoint — searches, resubmits, skips, reports, handoff settlements — newest
// first, loaded in pages via the audits-search continuation token.
const AuditTab = (props: IAuditTabProps) => {
  const [audits, setAudits] = useState<api.AuditEntry[]>([]);
  const [isLoading, setIsLoading] = useState<boolean>(true);
  const [continuationToken, setContinuationToken] = useState<
    string | undefined
  >(undefined);
  // Out-of-order guard — see events-panel's fetchTicket.
  const fetchTicket = useRef(0);

  const fetchAudits = useCallback(
    async (token?: string, append = false) => {
      const ticket = ++fetchTicket.current;
      setIsLoading(true);
      try {
        const client = new api.Client(api.CookieAuth());
        const request = new api.AuditSearchRequest();
        request.filter = new api.AuditSearchFilter({
          endpointId: props.endpointId,
        });
        request.continuationToken = token;
        request.maxItemCount = PAGE_SIZE;

        const response = await client.postAuditsSearch(request);
        if (ticket === fetchTicket.current) {
          const newAudits = response.audits ?? [];
          setAudits((prev) => (append ? [...prev, ...newAudits] : newAudits));
          setContinuationToken(response.continuationToken ?? undefined);
        }
      } catch (err) {
        if (ticket === fetchTicket.current) {
          console.error("Failed to fetch endpoint audits", err);
        }
      } finally {
        if (ticket === fetchTicket.current) {
          setIsLoading(false);
        }
      }
    },
    [props.endpointId],
  );

  useEffect(() => {
    setContinuationToken(undefined);
    fetchAudits();
  }, [fetchAudits]);

  const copyData = (data: string) => {
    navigator.clipboard
      ?.writeText(data)
      .then(() => notifyInfo("Audit data copied to clipboard"))
      .catch(() => {
        /* clipboard unavailable — ignore */
      });
  };

  const rows: ITableRow[] = audits.map((entry, index) => ({
    id: `${entry.eventId ?? "audit"}-${entry.createdAt?.toISOString() ?? index}-${index}`,
    route:
      entry.endpointId && entry.eventId
        ? `/Message/Index/${entry.endpointId}/${entry.eventId}/0`
        : undefined,
    data: new Map([
      [
        "createdAt",
        {
          value: formatMoment(entry.createdAt, true),
          searchValue: entry.createdAt?.valueOf() ?? 0,
        },
      ],
      [
        "auditType",
        {
          value: entry.auditType ?? "—",
          searchValue: entry.auditType ?? "",
        },
      ],
      [
        "auditorName",
        {
          value: entry.auditorName ?? "—",
          searchValue: entry.auditorName ?? "",
        },
      ],
      [
        "eventId",
        {
          value: entry.eventId ? <TruncatedGuid guid={entry.eventId} /> : "—",
          searchValue: entry.eventId ?? "",
        },
      ],
      [
        "data",
        {
          value: entry.data ? (
            <button
              type="button"
              onClick={(e) => {
                e.stopPropagation();
                copyData(entry.data!);
              }}
              title={entry.data}
              className="max-w-[280px] truncate text-left font-mono text-[11.5px] text-muted-foreground hover:text-foreground"
            >
              {entry.data}
            </button>
          ) : (
            "—"
          ),
          searchValue: entry.data ?? "",
        },
      ],
      [
        "accessDenied",
        {
          value: entry.accessDenied ? (
            <Badge variant="failed" size="sm" withDot={false}>
              Denied
            </Badge>
          ) : (
            ""
          ),
          searchValue: entry.accessDenied ? "denied" : "",
        },
      ],
    ]),
  }));

  return (
    <div className="flex flex-col w-full gap-2">
      <DataTable
        headCells={headCells}
        rows={rows}
        noDataMessage="No audit entries for this endpoint"
        isLoading={isLoading}
        orderBy="createdAt"
        order="desc"
        dataRowsPerPage={20}
        count={rows.length}
        fixedWidth={"-webkit-fill-available"}
      />
      {continuationToken && (
        <div className="flex justify-center">
          <Button
            variant="outline"
            size="sm"
            disabled={isLoading}
            onClick={() => fetchAudits(continuationToken, true)}
          >
            Load more
          </Button>
        </div>
      )}
    </div>
  );
};

export default AuditTab;
