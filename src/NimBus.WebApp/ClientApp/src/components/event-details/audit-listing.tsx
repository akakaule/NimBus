import * as React from "react";
import * as api from "api-client";
import { formatMoment } from "functions/endpoint.functions";
import DataTable, { ITableRow, ITableHeadCell } from "components/data-table";

interface IAuditListing {
  audits: api.MessageAudit[];
}

export default function AuditListing(props: IAuditListing) {
  const tableData = props.audits.map((audit) => {
    const row: ITableRow = {
      id: audit.auditTimestamp?.toLocaleString()!,
      data: new Map([
        [
          "auditTimestamp",
          {
            value: formatMoment(audit.auditTimestamp),
            searchValue: formatMoment(audit.auditTimestamp),
          },
        ],
        [
          "auditType",
          { value: audit.auditType!, searchValue: audit.auditType! },
        ],
        [
          "auditorName",
          { value: audit.auditorName!, searchValue: audit.auditorName! },
        ],
      ]),
    };
    return row;
  });

  const headCells: ITableHeadCell[] = [
    { id: "auditTimestamp", label: "Audit Timestamp", numeric: true },
    { id: "auditType", label: "Audit Type", numeric: false },
    { id: "auditorName", label: "Auditor Name", numeric: true },
  ];

  return (
    <DataTable
      headCells={headCells}
      rows={tableData || []}
      noDataMessage="No sessions available"
      isLoading={false}
      count={props.audits.length ?? 0}
    />
  );
}
