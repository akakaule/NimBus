import { useState, useEffect, useCallback, useMemo } from "react";
import * as api from "api-client";
import { formatMoment } from "functions/endpoint.functions";
import DataTable, { ITableRow, ITableHeadCell } from "components/data-table";
import Page from "components/page";
import TruncatedGuid from "components/common/truncated-guid";
import AuditFilterBar, {
  EMPTY_AUDIT_FILTER,
  type AuditFilterValues,
  toAuditSearchFilter,
} from "components/audits/audit-filter-bar";
import { useUrlFilters } from "hooks/use-url-filters";

enum Column {
  createdAt = "createdAt",
  auditType = "auditType",
  auditorName = "auditorName",
  eventId = "eventId",
  eventTypeId = "eventTypeId",
  endpointId = "endpointId",
}

const headCells: ITableHeadCell[] = [
  { id: Column.createdAt, label: "Timestamp", numeric: false },
  { id: Column.auditType, label: "Action", numeric: false },
  { id: Column.auditorName, label: "Auditor", numeric: false },
  { id: Column.eventId, label: "Event ID", numeric: false },
  { id: Column.eventTypeId, label: "Event Type", numeric: false },
  { id: Column.endpointId, label: "Endpoint", numeric: false },
];

function formatAuditType(type: string | undefined): string {
  switch (type) {
    case "Resubmit":
      return "Resubmit";
    case "ResubmitWithChanges":
      return "Resubmit with changes";
    case "Skip":
      return "Skip";
    case "Retry":
      return "Retry";
    case "Comment":
      return "Comment";
    default:
      return type ?? "-";
  }
}

function mapAuditToRow(entry: api.AuditEntry): ITableRow {
  const createdSortValue = entry.createdAt?.valueOf() ?? 0;
  const endpointId = entry.endpointId ?? "";
  const route = endpointId && entry.eventId
    ? `/Message/Index/${endpointId}/${entry.eventId}/0`
    : undefined;

  return {
    id: `${entry.eventId}-${entry.createdAt?.toISOString()}`,
    route,
    data: new Map([
      [
        Column.createdAt,
        {
          value: formatMoment(entry.createdAt),
          searchValue: createdSortValue,
        },
      ],
      [
        Column.auditType,
        {
          value: formatAuditType(entry.auditType),
          searchValue: entry.auditType ?? "",
        },
      ],
      [
        Column.auditorName,
        {
          value: entry.auditorName ?? "-",
          searchValue: entry.auditorName ?? "",
        },
      ],
      [
        Column.eventId,
        {
          value: entry.eventId ? (
            <TruncatedGuid guid={entry.eventId} />
          ) : (
            "-"
          ),
          searchValue: entry.eventId ?? "",
        },
      ],
      [
        Column.eventTypeId,
        {
          value: entry.eventTypeId ?? "-",
          searchValue: entry.eventTypeId ?? "",
        },
      ],
      [
        Column.endpointId,
        {
          value: endpointId || "-",
          searchValue: endpointId,
        },
      ],
    ]),
  };
}

// Page-size options for the Audit Log search. Server clamps to [1, 200].
const PAGE_SIZE_OPTIONS = [25, 50, 100, 200] as const;
const DEFAULT_PAGE_SIZE = 50;

export default function AuditsList() {
  // URL is the source of truth for the applied filter; browser Back/forward restores it for free.
  const { applied, applyFilters, resetFilters } =
    useUrlFilters<AuditFilterValues>(EMPTY_AUDIT_FILTER);

  const [audits, setAudits] = useState<api.AuditEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [continuationToken, setContinuationToken] = useState<
    string | undefined
  >(undefined);
  const [pageSize, setPageSize] = useState<number>(DEFAULT_PAGE_SIZE);

  const apiFilter = useMemo(() => toAuditSearchFilter(applied), [applied]);

  const fetchAudits = useCallback(
    async (
      filter: api.AuditSearchFilter,
      size: number,
      token?: string,
      append = false,
    ) => {
      setLoading(true);
      try {
        const client = new api.Client(api.CookieAuth());
        const request = new api.AuditSearchRequest();
        request.filter = filter;
        request.continuationToken = token;
        request.maxItemCount = size;

        const response = await client.postAuditsSearch(request);
        const newAudits = response.audits ?? [];

        if (append) {
          setAudits((prev) => [...prev, ...newAudits]);
        } else {
          setAudits(newAudits);
        }
        setContinuationToken(response.continuationToken ?? undefined);
      } catch (err) {
        console.error("Failed to fetch audits", err);
      } finally {
        setLoading(false);
      }
    },
    [],
  );

  useEffect(() => {
    setContinuationToken(undefined);
    fetchAudits(apiFilter, pageSize);
  }, [apiFilter, pageSize, fetchAudits]);

  const handlePageChange = () => {
    if (continuationToken) {
      fetchAudits(apiFilter, pageSize, continuationToken, true);
    }
  };

  const rows = audits.map(mapAuditToRow);

  return (
    <Page title="Audit Log">
      <div className="flex flex-col w-full">
        <AuditFilterBar
          value={applied}
          onSearch={(next) => applyFilters(next)}
          onReset={resetFilters}
          isLoading={loading}
        />
        <div className="flex justify-end items-center gap-2 px-2 py-1 text-sm text-muted-foreground">
          <label htmlFor="audits-page-size">Rows per page:</label>
          <select
            id="audits-page-size"
            className="bg-card text-foreground border border-border rounded px-2 py-1"
            value={pageSize}
            onChange={(e) => setPageSize(Number(e.target.value))}
            disabled={loading}
          >
            {PAGE_SIZE_OPTIONS.map((n) => (
              <option key={n} value={n}>
                {n}
              </option>
            ))}
          </select>
        </div>
        <DataTable
          headCells={headCells}
          rows={rows}
          noDataMessage="No audit entries found"
          isLoading={loading}
          orderBy={Column.createdAt}
          order="desc"
          dataRowsPerPage={pageSize}
          count={rows.length}
          onPageChange={handlePageChange}
        />
      </div>
    </Page>
  );
}
