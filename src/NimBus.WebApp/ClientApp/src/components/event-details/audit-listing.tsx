import { useCallback, useEffect, useRef, useState } from "react";
import * as api from "api-client";
import { formatMoment } from "functions/endpoint.functions";
import DataTable, { ITableRow, ITableHeadCell } from "components/data-table";
import { notifySuccess, notifyError } from "functions/notifications.functions";
import { Badge, Button, Tooltip } from "components/ui";

interface IAuditListing {
  /**
   * Required, not just for filtering: /api/audits/search authorizes an
   * endpoint-scoped query against Reader on that endpoint, and demands site
   * Owner when no endpoint is supplied. Passing it keeps this tab usable by
   * anyone who can already read the event.
   */
  endpointId: string;
  eventId: string;
}

// One page is plenty for the common case (a handful of rows per event); the
// server clamps maxItemCount to [1, 200] regardless of what we ask for.
const DEFAULT_TOP = 30;
const PAGE_SIZE = 200;

export default function AuditListing(props: IAuditListing) {
  const [audits, setAudits] = useState<api.AuditEntry[]>([]);
  const [showingTop, setShowingTop] = useState(false);
  const [loading, setLoading] = useState(true);
  const [truncated, setTruncated] = useState(false);

  // Bumped per fetch so a slow "top" response can't overwrite a newer
  // "fetch all" result (the same guard the Audit Log page uses).
  const fetchTicket = useRef(0);

  const fetchAudits = useCallback(
    async (all: boolean) => {
      const ticket = ++fetchTicket.current;
      setLoading(true);
      try {
        const client = new api.Client(api.CookieAuth());
        const filter = new api.AuditSearchFilter();
        filter.eventId = props.eventId;
        filter.endpointId = props.endpointId;

        const collected: api.AuditEntry[] = [];
        let token: string | undefined = undefined;
        let more = true;
        while (more) {
          const request = new api.AuditSearchRequest();
          request.filter = filter;
          request.continuationToken = token;
          request.maxItemCount = all ? PAGE_SIZE : DEFAULT_TOP;

          const response = await client.postAuditsSearch(request);
          collected.push(...(response.audits ?? []));
          token = response.continuationToken ?? undefined;
          // "Fetch All" walks the continuation tokens; the default view stops
          // after the first page and offers the button instead.
          more = all && token !== undefined;
        }

        if (ticket !== fetchTicket.current) return;
        setAudits(collected);
        // Only offer "Fetch All" when the server says there is more to fetch.
        setShowingTop(!all && token !== undefined);
        setTruncated(false);
      } catch (err) {
        if (ticket !== fetchTicket.current) return;
        console.error("Failed to fetch audits", err);
        setAudits([]);
        setShowingTop(false);
        setTruncated(true);
      } finally {
        if (ticket === fetchTicket.current) setLoading(false);
      }
    },
    [props.endpointId, props.eventId],
  );

  useEffect(() => {
    void fetchAudits(false);
  }, [fetchAudits]);

  const copy = (text: string) => {
    navigator.clipboard
      .writeText(text)
      .then(() => notifySuccess("Copied to clipboard"))
      .catch(() => notifyError("Could not copy to clipboard"));
  };

  const tableData: ITableRow[] = audits.map((audit, i) => ({
    // Timestamps can collide when several rows are written in one action, so
    // the index keeps React keys unique.
    id: `${audit.auditTimestamp?.toISOString() ?? ""}-${i}`,
    data: new Map([
      [
        "auditTimestamp",
        {
          value: formatMoment(audit.auditTimestamp),
          searchValue: audit.auditTimestamp?.toISOString() ?? "",
        },
      ],
      [
        "auditType",
        { value: audit.auditType ?? "", searchValue: audit.auditType ?? "" },
      ],
      [
        "auditorName",
        {
          value: audit.auditorName ?? "",
          searchValue: audit.auditorName ?? "",
        },
      ],
      [
        "comment",
        { value: audit.comment ?? "", searchValue: audit.comment ?? "" },
      ],
      [
        "data",
        {
          value: audit.data ? (
            <Tooltip content="Copy to clipboard" position="top">
              <span
                className="cursor-pointer font-mono text-[12px] break-all"
                onClick={() => copy(audit.data ?? "")}
              >
                {audit.data}
              </span>
            </Tooltip>
          ) : (
            ""
          ),
          searchValue: audit.data ?? "",
        },
      ],
      [
        "accessDenied",
        {
          // A denied row is the one an operator scans for, so it reads as a
          // badge rather than the word "true" buried in a column of "false".
          value: audit.accessDenied ? (
            <Badge variant="error">Denied</Badge>
          ) : (
            "—"
          ),
          searchValue: audit.accessDenied ? "true denied" : "false",
        },
      ],
    ]),
  }));

  const headCells: ITableHeadCell[] = [
    { id: "auditTimestamp", label: "Audit Timestamp", numeric: true, width: "15%" },
    { id: "auditType", label: "Audit Type", numeric: false, width: "14%" },
    { id: "auditorName", label: "Auditor Name", numeric: false, width: "16%" },
    { id: "comment", label: "Comment", numeric: false, width: "22%" },
    { id: "data", label: "Data", numeric: false, width: "23%" },
    { id: "accessDenied", label: "Access Denied", numeric: false, width: "10%" },
  ];

  return (
    <div className="flex flex-col items-start gap-1 w-full">
      <DataTable
        headCells={headCells}
        rows={tableData}
        noDataMessage={
          truncated
            ? "Could not load the audit trail for this event."
            : "No audit entries for this event."
        }
        isLoading={loading}
        count={audits.length}
      />

      {showingTop && (
        <div className="flex items-center gap-3 mt-2">
          <span className="text-[13px] text-muted-foreground">
            {`Showing top ${DEFAULT_TOP} audits`}
          </span>
          <Button
            onClick={() => void fetchAudits(true)}
            aria-label="Fetch All"
            leftIcon={
              <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none">
                <path
                  d="M1.5 8s2.4-4 6.5-4 6.5 4 6.5 4-2.4 4-6.5 4-6.5-4-6.5-4Z"
                  stroke="currentColor"
                  strokeWidth="1.4"
                />
                <circle cx="8" cy="8" r="1.8" stroke="currentColor" strokeWidth="1.4" />
              </svg>
            }
            colorScheme="primary"
          >
            Fetch All
          </Button>
        </div>
      )}
    </div>
  );
}
