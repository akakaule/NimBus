import { useState, useEffect, useCallback } from "react";
import * as api from "api-client";
import { formatMoment } from "functions/endpoint.functions";
import DataTable, { ITableRow, ITableHeadCell } from "components/data-table";
import Page from "components/page";
import TruncatedGuid from "components/common/truncated-guid";
import AuditFilterBar from "components/audits/audit-filter-bar";

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

export default function AuditsList() {
  const [audits, setAudits] = useState<api.AuditEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [continuationToken, setContinuationToken] = useState<
    string | undefined
  >(undefined);
  const [currentFilter, setCurrentFilter] = useState<api.AuditSearchFilter>(
    () => new api.AuditSearchFilter(),
  );

  const fetchAudits = useCallback(
    async (filter: api.AuditSearchFilter, token?: string, append = false) => {
      setLoading(true);
      try {
        const client = new api.Client(api.CookieAuth());
        const request = new api.AuditSearchRequest();
        request.filter = filter;
        request.continuationToken = token;
        request.maxItemCount = 50;

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
    fetchAudits(currentFilter);
  }, []);

  const handleSearch = (filter: api.AuditSearchFilter) => {
    setCurrentFilter(filter);
    setContinuationToken(undefined);
    fetchAudits(filter);
  };

  const handlePageChange = () => {
    if (continuationToken) {
      fetchAudits(currentFilter, continuationToken, true);
    }
  };

  const rows = audits.map(mapAuditToRow);

  return (
    <Page title="Audit Log">
      <div className="flex flex-col w-full">
        <AuditFilterBar onSearch={handleSearch} isLoading={loading} />
        <DataTable
          headCells={headCells}
          rows={rows}
          noDataMessage="No audit entries found"
          isLoading={loading}
          orderBy={Column.createdAt}
          order="desc"
          dataRowsPerPage={50}
          count={rows.length}
          onPageChange={handlePageChange}
        />
      </div>
    </Page>
  );
}
