import { useState, useEffect, useCallback, useMemo } from "react";
import * as api from "api-client";
import { formatMoment } from "functions/endpoint.functions";
import DataTable, { ITableRow, ITableHeadCell } from "components/data-table";
import Page from "components/page";
import TruncatedGuid from "components/common/truncated-guid";
import MessageFilterBar, {
  EMPTY_MESSAGE_FILTER,
  type MessageFilterValues,
  toMessageSearchFilter,
} from "components/messages/message-filter-bar";
import { useUrlFilters } from "hooks/use-url-filters";

enum Column {
  eventId = "eventId",
  messageId = "messageId",
  sessionId = "sessionId",
  eventType = "eventType",
  messageType = "messageType",
  timestamp = "timestamp",
  to = "to",
  from = "from",
}

const headCells: ITableHeadCell[] = [
  { id: Column.eventId, label: "Event ID", numeric: false },
  { id: Column.messageId, label: "Message ID", numeric: false },
  { id: Column.sessionId, label: "Session ID", numeric: false },
  { id: Column.eventType, label: "Event Type", numeric: false },
  { id: Column.messageType, label: "Type", numeric: false },
  { id: Column.timestamp, label: "Enqueued", numeric: false },
  { id: Column.from, label: "From", numeric: false },
  { id: Column.to, label: "To", numeric: false },
];

type IdFilterField = "eventId" | "messageId" | "sessionId";

function mapMessageToRow(
  msg: api.Message,
  onFilterById: (field: IdFilterField, value: string) => void,
): ITableRow {
  const enqueuedSortValue = msg.enqueuedTimeUtc?.valueOf() ?? 0;

  return {
    id: msg.messageId ?? msg.eventId ?? "",
    route: `/Message/Index/${msg.endpointId ?? msg.to}/${msg.eventId}/0`,
    data: new Map([
      [
        Column.eventId,
        {
          value: (
            <TruncatedGuid
              guid={msg.eventId}
              onClick={(g) => onFilterById("eventId", g)}
            />
          ),
          searchValue: msg.eventId ?? "",
        },
      ],
      [
        Column.messageId,
        {
          value: (
            <TruncatedGuid
              guid={msg.messageId}
              onClick={(g) => onFilterById("messageId", g)}
            />
          ),
          searchValue: msg.messageId ?? "",
        },
      ],
      [
        Column.sessionId,
        {
          value: (
            <TruncatedGuid
              guid={msg.sessionId}
              onClick={(g) => onFilterById("sessionId", g)}
            />
          ),
          searchValue: msg.sessionId ?? "",
        },
      ],
      [
        Column.eventType,
        {
          value: msg.eventTypeId ?? "-",
          searchValue: msg.eventTypeId ?? "",
        },
      ],
      [
        Column.messageType,
        {
          value: msg.messageType ?? "-",
          searchValue: msg.messageType?.toString() ?? "",
        },
      ],
      [
        Column.timestamp,
        {
          value: formatMoment(msg.enqueuedTimeUtc, true),
          searchValue: enqueuedSortValue,
        },
      ],
      [
        Column.to,
        {
          value: msg.to ?? "-",
          searchValue: msg.to ?? "",
        },
      ],
      [
        Column.from,
        {
          value: msg.originatingFrom || msg.from || "-",
          searchValue: msg.originatingFrom || msg.from || "",
        },
      ],
    ]),
  };
}

// Page-size options for the Messages search. Server clamps to [1, 200].
const PAGE_SIZE_OPTIONS = [25, 50, 100, 200] as const;
const DEFAULT_PAGE_SIZE = 50;

export default function MessagesList() {
  // URL is the source of truth for the applied filter — useUrlFilters reads from
  // and writes to query params, so browser Back/forward restores the filter for free.
  const { applied, applyFilters, resetFilters } =
    useUrlFilters<MessageFilterValues>(EMPTY_MESSAGE_FILTER);

  const [messages, setMessages] = useState<api.Message[]>([]);
  const [loading, setLoading] = useState(true);
  const [continuationToken, setContinuationToken] = useState<
    string | undefined
  >(undefined);
  const [pageSize, setPageSize] = useState<number>(DEFAULT_PAGE_SIZE);

  const apiFilter = useMemo(() => toMessageSearchFilter(applied), [applied]);

  const fetchMessages = useCallback(
    async (
      filter: api.MessageSearchFilter,
      size: number,
      token?: string,
      append = false,
    ) => {
      setLoading(true);
      try {
        const client = new api.Client(api.CookieAuth());
        const request = new api.MessageSearchRequest();
        request.filter = filter;
        request.continuationToken = token;
        request.maxItemCount = size;

        const response = await client.postMessagesSearch(request);
        const newMessages = response.messages ?? [];

        if (append) {
          setMessages((prev) => [...prev, ...newMessages]);
        } else {
          setMessages(newMessages);
        }
        setContinuationToken(response.continuationToken ?? undefined);
      } catch (err) {
        console.error("Failed to fetch messages", err);
      } finally {
        setLoading(false);
      }
    },
    [],
  );

  // Re-fetch whenever the applied filter or page size changes — covers initial mount, Search,
  // Reset, browser Back/forward, and direct URL-bookmark loads.
  useEffect(() => {
    setContinuationToken(undefined);
    fetchMessages(apiFilter, pageSize);
  }, [apiFilter, pageSize, fetchMessages]);

  const handlePageChange = () => {
    if (continuationToken && !loading) {
      fetchMessages(apiFilter, pageSize, continuationToken, true);
    }
  };

  // Clicking an ID cell narrows the current filter set by setting just that
  // field; other fields stay as the user left them (URL-driven merge behavior).
  const filterByField = useCallback(
    (field: IdFilterField, value: string) => {
      applyFilters({ ...applied, [field]: value });
    },
    [applied, applyFilters],
  );

  const rows = messages.map((m) => mapMessageToRow(m, filterByField));

  return (
    <Page title="Messages">
      <div className="flex flex-col w-full">
        <MessageFilterBar
          value={applied}
          onSearch={(next) => applyFilters(next)}
          onReset={resetFilters}
          isLoading={loading}
        />
        <div className="flex justify-end items-center gap-2 px-2 py-1 text-sm text-muted-foreground">
          <label htmlFor="messages-page-size">Rows per page:</label>
          <select
            id="messages-page-size"
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
          noDataMessage="No messages found"
          isLoading={loading}
          orderBy={Column.timestamp}
          order="desc"
          dataRowsPerPage={pageSize}
          count={rows.length}
          onPageChange={handlePageChange}
        />
      </div>
    </Page>
  );
}
