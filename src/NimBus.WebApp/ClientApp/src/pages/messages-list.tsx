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

function mapMessageToRow(msg: api.Message): ITableRow {
  const enqueuedSortValue = msg.enqueuedTimeUtc?.valueOf() ?? 0;

  return {
    id: msg.messageId ?? msg.eventId ?? "",
    route: `/Message/Index/${msg.endpointId ?? msg.to}/${msg.eventId}/0`,
    data: new Map([
      [
        Column.eventId,
        {
          value: <TruncatedGuid guid={msg.eventId} />,
          searchValue: msg.eventId ?? "",
        },
      ],
      [
        Column.messageId,
        {
          value: <TruncatedGuid guid={msg.messageId} />,
          searchValue: msg.messageId ?? "",
        },
      ],
      [
        Column.sessionId,
        {
          value: <TruncatedGuid guid={msg.sessionId} />,
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
          value: msg.originatingFrom ?? msg.from ?? "-",
          searchValue: msg.originatingFrom ?? msg.from ?? "",
        },
      ],
    ]),
  };
}

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

  const apiFilter = useMemo(() => toMessageSearchFilter(applied), [applied]);

  const fetchMessages = useCallback(
    async (filter: api.MessageSearchFilter, token?: string, append = false) => {
      setLoading(true);
      try {
        const client = new api.Client(api.CookieAuth());
        const request = new api.MessageSearchRequest();
        request.filter = filter;
        request.continuationToken = token;
        request.maxItemCount = 50;

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

  // Re-fetch whenever the applied filter changes — covers initial mount, Search,
  // Reset, browser Back/forward, and direct URL-bookmark loads.
  useEffect(() => {
    setContinuationToken(undefined);
    fetchMessages(apiFilter);
  }, [apiFilter, fetchMessages]);

  const handlePageChange = () => {
    if (continuationToken && !loading) {
      fetchMessages(apiFilter, continuationToken, true);
    }
  };

  const rows = messages.map(mapMessageToRow);

  return (
    <Page title="Messages">
      <div className="flex flex-col w-full">
        <MessageFilterBar
          value={applied}
          onSearch={(next) => applyFilters(next)}
          onReset={resetFilters}
          isLoading={loading}
        />
        <DataTable
          headCells={headCells}
          rows={rows}
          noDataMessage="No messages found"
          isLoading={loading}
          orderBy={Column.timestamp}
          order="desc"
          dataRowsPerPage={50}
          count={rows.length}
          onPageChange={handlePageChange}
        />
      </div>
    </Page>
  );
}
