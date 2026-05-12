import * as React from "react";
import * as api from "api-client";
import { formatMoment } from "functions/endpoint.functions";
import { BlockedEvent } from "pages/event-details";
import DataTable, {
  ITableHeadAction,
  ITableRow,
  ITableHeadCell,
} from "components/data-table";

export interface IBlockedListing {
  events: BlockedEvent[];
  onPageChange?: () => void;
  // skip/take match the server-side pagination contract on /api/event/blocked/{endpointId}/{sessionId}.
  fetchBlockedEvents: (skip: number, take: number) => void;
  totalItems: number;
  // Fallback endpointId — blocked events are siblings on the same session, so
  // they share the parent event's endpoint even when their per-message
  // endpointId/to fields are not projected onto the enriched Message.
  endpointId?: string;
}

export default function BlockedListing(props: IBlockedListing) {
  const count = props.totalItems ?? 0;
  const [page, setPage] = React.useState(0);
  const [rowsPerPage, setRowsPerPage] = React.useState(6);
  const [rows, setRows] = React.useState<ITableRow[]>([]);

  React.useEffect(() => {
    setRows(mapEvents());
  }, [props.events]);

  const headCells: ITableHeadCell[] = [
    { id: "eventId", label: "Event Id", numeric: false },
    { id: "sessionId", label: "Session Id", numeric: false },
    { id: "eventTypeId", label: "Event Type", numeric: false },
    { id: "added", label: "Added(UTC)", numeric: false },
  ];

  const headActions: ITableHeadAction[] = [];

  const mapEvents = () => {
    const iRows = props.events.map((item) => {
      const endpointId =
        item.message.endpointId || item.message.to || props.endpointId;
      const row: ITableRow = {
        id: item.message.eventId!,
        bodyActions: [],
        route: endpointId
          ? `/Message/Index/${endpointId}/${item.message.eventId}/0`
          : undefined,
        data: new Map([
          [
            "eventId",
            {
              value: item.message.eventId!,
              searchValue: item.message.eventId!,
            },
          ],
          [
            "sessionId",
            {
              value: item.message.sessionId!,
              searchValue: item.message.sessionId!,
            },
          ],
          [
            "eventTypeId",
            {
              value: item?.message.eventTypeId,
              searchValue: item?.message.eventTypeId || "",
            },
          ],
          [
            "added",
            {
              value: formatMoment(item.message.enqueuedTimeUtc!, true),
              searchValue: formatMoment(item.message.enqueuedTimeUtc!, true),
            },
          ],
        ]),
      };
      return row;
    });
    return iRows;
  };

  return (
    <div className="w-full mr-4 flex flex-col">
      <DataTable
        headCells={headCells}
        headActions={headActions}
        rows={rows}
        withCheckboxes={false}
        noDataMessage="No events available"
        isLoading={false}
        count={rows.length}
        dataRowsPerPage={20}
      />
    </div>
  );
}
