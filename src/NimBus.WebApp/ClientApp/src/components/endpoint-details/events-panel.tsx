import * as React from "react";
import * as api from "api-client";
import DataTable, {
  ITableHeadAction,
  ITableRow,
  ITableBodyAction,
  ITableHeadCell,
} from "components/data-table";
import { useParams } from "react-router-dom";
import { formatMoment } from "functions/endpoint.functions";
import EventFiltering from "./filter/event-filtering";
import FilterContext, { IFilterContext } from "./filter/filtering-context";
import TruncatedGuid from "components/common/truncated-guid";
import ErrorGroupedView from "./error-grouped-view";

interface EventsPanelProps {
  endpointId: string;
}

interface SessionState {
  deferredCount: number;
  pendingCount: number;
  failedCount: number;
}

const ACTIONABLE_STATUSES = [
  api.ResolutionStatus.Failed,
  api.ResolutionStatus.DeadLettered,
  api.ResolutionStatus.Unsupported,
  api.ResolutionStatus.Deferred,
];

const DEFAULT_FAILED_STATUSES: api.ResolutionStatus[] = [
  api.ResolutionStatus.Failed,
  api.ResolutionStatus.DeadLettered,
];

const EventsPanel = (props: EventsPanelProps) => {
  const client = new api.Client(api.CookieAuth());
  const params = useParams();
  const [events, setEvents] = React.useState<api.Event[]>([]);
  const [sessions, setSessions] = React.useState<Record<string, SessionState>>(
    {},
  );
  const [rows, setRows] = React.useState<ITableRow[]>([]);
  const [continuationToken, setContinuationToken] = React.useState<
    string | undefined
  >();
  const [isLoading, setIsLoading] = React.useState<boolean>(true);
  const [currentFilter, setCurrentFilter] = React.useState<
    api.EventFilter | undefined
  >();
  const [maxResults, setMaxResults] = React.useState<number>(100);
  const hasFetchedRef = React.useRef(false);
  const [viewMode, setViewMode] = React.useState<"list" | "grouped">("list");
  const [initialStatuses, setInitialStatuses] = React.useState<
    api.ResolutionStatus[]
  >([...DEFAULT_FAILED_STATUSES]);

  const endpointId = props.endpointId || params.id!;

  // Initialize filter with failed statuses
  const [eventFilter, setEventFilter] = React.useState<api.EventFilter>(() => {
    const filter = new api.EventFilter();
    filter.endpointId = endpointId;
    filter.resolutionStatus = [...DEFAULT_FAILED_STATUSES];
    return filter;
  });

  const context: IFilterContext = {
    filterContext: eventFilter,
    setProjectContext: setEventFilter,
  };

  // Auto-fetch failed events on mount
  React.useEffect(() => {
    if (hasFetchedRef.current || !endpointId) {
      return;
    }
    hasFetchedRef.current = true;
    fetchEvents(eventFilter);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [endpointId]);

  // Update rows when events or sessions change
  React.useEffect(() => {
    setRows(mapEvents());
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [events, sessions]);

  const isActionableStatus = (status: string | undefined): boolean => {
    if (!status) return false;
    return ACTIONABLE_STATUSES.includes(status as api.ResolutionStatus);
  };

  const getSessions = (
    deferredEvents: string[],
    pendingEvents: string[],
  ): Record<string, SessionState> => {
    const tempSessions: Record<string, SessionState> = {};

    deferredEvents.forEach((event) => {
      const eventSessionSplit = event.split("_");
      const sessionId = eventSessionSplit[1];
      if (tempSessions[sessionId] !== undefined) {
        tempSessions[sessionId].deferredCount += 1;
      } else {
        tempSessions[sessionId] = {
          deferredCount: 1,
          pendingCount: 0,
          failedCount: 0,
        };
      }
    });

    pendingEvents.forEach((event) => {
      const eventSessionSplit = event.split("_");
      const sessionId = eventSessionSplit[1];
      if (tempSessions[sessionId] !== undefined) {
        tempSessions[sessionId].pendingCount += 1;
      } else {
        tempSessions[sessionId] = {
          deferredCount: 0,
          pendingCount: 1,
          failedCount: 0,
        };
      }
    });

    return tempSessions;
  };

  const fetchSessionStatus = async (
    actionableEvents: api.Event[],
  ): Promise<void> => {
    const uniqueSessionIds = [
      ...new Set(
        actionableEvents
          .map((e) => e.sessionId)
          .filter((id): id is string => !!id),
      ),
    ];

    if (uniqueSessionIds.length === 0) return;

    try {
      const sessionStatuses = await client.postEndpointSessionsBatch(
        endpointId,
        uniqueSessionIds,
      );

      const deferredEvents = sessionStatuses
        .map((status) => status.deferredEvents ?? [])
        .reduce((pre, cur) => pre.concat(cur), []);

      const pendingEvents = sessionStatuses
        .map((status) => status.pendingEvents ?? [])
        .reduce((pre, cur) => pre.concat(cur), []);

      const tempSessions = getSessions(deferredEvents, pendingEvents);
      setSessions((prev) => ({ ...prev, ...tempSessions }));
    } catch {
      console.error("Failed to fetch session statuses in batch");
    }
  };

  const fetchEvents = async (filter: api.EventFilter): Promise<void> => {
    setIsLoading(true);
    try {
      const reqBody = new api.SearchRequest();
      reqBody.eventFilter = filter;
      reqBody.maxSearchItemsCount = maxResults;

      const response = await client.postApiEventEndpointIdGetByFilter(
        endpointId,
        reqBody,
      );
      const fetchedEvents = response.events ?? [];

      setEvents(fetchedEvents);
      setContinuationToken(response.continuationToken);
      setCurrentFilter(filter);

      // Fetch session status only for actionable events
      const actionableEvents = fetchedEvents.filter((e) =>
        isActionableStatus(e.resolutionStatus),
      );

      if (actionableEvents.length > 0) {
        await fetchSessionStatus(actionableEvents);
      }
    } catch (error) {
      console.error("Failed to fetch events:", error);
      setEvents([]);
    } finally {
      setIsLoading(false);
    }
  };

  const nextPage = async (): Promise<void> => {
    if (
      !continuationToken ||
      continuationToken === "" ||
      continuationToken === "null" ||
      !currentFilter
    ) {
      return;
    }

    try {
      const reqBody = new api.SearchRequest();
      reqBody.eventFilter = currentFilter;
      reqBody.continuationToken = continuationToken;
      reqBody.maxSearchItemsCount = maxResults;

      const response = await client.postApiEventEndpointIdGetByFilter(
        endpointId,
        reqBody,
      );
      const newEvents = response.events ?? [];

      setContinuationToken(response.continuationToken);

      // Fetch session status for new actionable events
      const actionableEvents = newEvents.filter((e) =>
        isActionableStatus(e.resolutionStatus),
      );

      if (actionableEvents.length > 0) {
        await fetchSessionStatus(actionableEvents);
      }

      setEvents((prev) => [...prev, ...newEvents]);
    } catch (error) {
      console.error("Failed to fetch next page:", error);
    }
  };

  const removeFromTable = (event: api.Event): void => {
    setEvents((prev) => prev.filter((e) => e.eventId !== event.eventId));
  };

  const skipSingleEvent = (event: api.Event): void => {
    removeFromTable(event);
    client.postSkipEventIds(event.eventId!, event.lastMessageId!);
  };

  const resubmitSingleEvent = (event: api.Event): void => {
    removeFromTable(event);
    client.postResubmitEventIds(event.eventId!, event.lastMessageId!);
  };

  const reprocessDeferredBySession = async (event: api.Event): Promise<void> => {
    const sessionId = event.sessionId;
    if (!sessionId) return;
    try {
      await client.postReprocessDeferred(endpointId, sessionId);
      setEvents((prev) =>
        prev.filter(
          (e) =>
            !(e.sessionId === sessionId && e.resolutionStatus === api.ResolutionStatus.Deferred),
        ),
      );
    } catch (err) {
      console.error("Failed to reprocess deferred messages for session", sessionId, err);
    }
  };

  const getViableBodyActions = (event: api.Event): ITableBodyAction[] => {
    if (!isActionableStatus(event.resolutionStatus)) {
      return [];
    }

    const actions: ITableBodyAction[] = [];

    if (event.resolutionStatus === api.ResolutionStatus.Deferred) {
      actions.push({
        name: "Reprocess",
        onClick: () => {
          reprocessDeferredBySession(event);
          return false;
        },
      });
    } else {
      actions.push(
        {
          name: "Resubmit",
          onClick: () => {
            resubmitSingleEvent(event);
            return false;
          },
        },
        {
          name: "Skip",
          onClick: () => {
            skipSingleEvent(event);
            return false;
          },
        },
      );
    }

    return actions;
  };

  const mapEvents = (): ITableRow[] => {
    return events.map((item) => {
      const hasSessionData = isActionableStatus(item.resolutionStatus);
      const sessionData = sessions[item.sessionId!];

      const row: ITableRow = {
        id: item.eventId!,
        route: `/Message/Index/${endpointId}/${item.eventId}/0`,
        bodyActions: getViableBodyActions(item),
        data: new Map([
          [
            "eventId",
            {
              value: <TruncatedGuid guid={item.eventId} />,
              searchValue: item.eventId!,
            },
          ],
          [
            "pendingCount",
            {
              value: hasSessionData ? (sessionData?.pendingCount ?? 0) : "-",
              searchValue: hasSessionData
                ? (sessionData?.pendingCount ?? 0)
                : 0,
            },
          ],
          [
            "deferredCount",
            {
              value: hasSessionData ? (sessionData?.deferredCount ?? 0) : "-",
              searchValue: hasSessionData
                ? (sessionData?.deferredCount ?? 0)
                : 0,
            },
          ],
          [
            "status",
            {
              value: item.resolutionStatus,
              searchValue: item.resolutionStatus ?? "",
            },
          ],
          [
            "sessionId",
            {
              value: <TruncatedGuid guid={item?.sessionId} />,
              searchValue: item?.sessionId || "",
            },
          ],
          [
            "eventTypeId",
            {
              value: item?.eventTypeId,
              searchValue: item?.eventTypeId || "",
            },
          ],
          [
            "updated",
            {
              value: formatMoment(item?.updatedAt, true),
              searchValue: formatMoment(item?.updatedAt) || "",
            },
          ],
          [
            "added",
            {
              value: formatMoment(item?.enqueuedTimeUtc, true),
              searchValue: formatMoment(item?.enqueuedTimeUtc) || "",
            },
          ],
        ]),
      };
      return row;
    });
  };

  const doActionSelectedRows = (
    selectedRows: ITableRow[],
    actionName: string,
  ): void => {
    selectedRows.forEach((row) => {
      const action = row.bodyActions?.find((a) => a.name === actionName);
      action?.onClick();
    });
  };

  const hasActionableSelected = (selectedRows: ITableRow[]): boolean => {
    return selectedRows.some(
      (row) => row.bodyActions && row.bodyActions.length > 0,
    );
  };

  const handleFilterClicked = async (
    filter: api.EventFilter,
  ): Promise<void> => {
    setSessions({});
    await fetchEvents(filter);
  };

  const handleReset = (): void => {
    const resetFilter = new api.EventFilter();
    resetFilter.endpointId = endpointId;
    resetFilter.resolutionStatus = [...DEFAULT_FAILED_STATUSES];
    setInitialStatuses([...DEFAULT_FAILED_STATUSES]);
    setEventFilter(resetFilter);
    setSessions({});
    fetchEvents(resetFilter);
  };

  const handleClearStatus = (): void => {
    const clearStatusFilter = eventFilter.clone();
    clearStatusFilter.resolutionStatus = [];
    setInitialStatuses([]);
    setEventFilter(clearStatusFilter);
    setSessions({});
    fetchEvents(clearStatusFilter);
  };

  const headCells: ITableHeadCell[] = [
    { id: "eventId", label: "Event Id", numeric: false, width: "10%" },
    { id: "pendingCount", label: "Pending", numeric: true, width: "6%" },
    { id: "deferredCount", label: "Deferred", numeric: true, width: "7%" },
    { id: "status", label: "Status", numeric: false, width: "8%" },
    { id: "sessionId", label: "Session Id", numeric: false, width: "10%" },
    { id: "eventTypeId", label: "Event Type", numeric: false, width: "15%" },
    { id: "updated", label: "Updated(UTC)", numeric: false, width: "12%" },
    { id: "added", label: "Added(UTC)", numeric: false, width: "12%" },
  ];

  const headActions: ITableHeadAction[] = [
    {
      name: "Resubmit",
      onClick: (selectedRows: ITableRow[]) => {
        if (hasActionableSelected(selectedRows)) {
          const actionableRows = selectedRows.filter(
            (row) => row.bodyActions && row.bodyActions.length > 0,
          );
          doActionSelectedRows(actionableRows, "Resubmit");
        }
        return false;
      },
    },
    {
      name: "Skip",
      onClick: (selectedRows: ITableRow[]) => {
        if (hasActionableSelected(selectedRows)) {
          const actionableRows = selectedRows.filter(
            (row) => row.bodyActions && row.bodyActions.length > 0,
          );
          doActionSelectedRows(actionableRows, "Skip");
        }
        return false;
      },
    },
  ];

  return (
    <FilterContext.Provider value={context}>
      <div className="flex flex-col gap-4">
        <EventFiltering
          handleFilterClicked={handleFilterClicked}
          onReset={handleReset}
          onClearStatus={handleClearStatus}
          initialStatuses={initialStatuses}
          maxResults={maxResults}
          onMaxResultsChange={setMaxResults}
        />
        <div className="flex items-center gap-2 text-sm">
          <span className="text-muted-foreground">View:</span>
          <button
            className={`px-3 py-1 rounded-md border text-xs font-medium ${viewMode === "list" ? "bg-primary text-white border-primary" : "bg-card text-foreground border-border"}`}
            onClick={() => setViewMode("list")}
          >
            List
          </button>
          <button
            className={`px-3 py-1 rounded-md border text-xs font-medium ${viewMode === "grouped" ? "bg-primary text-white border-primary" : "bg-card text-foreground border-border"}`}
            onClick={() => setViewMode("grouped")}
          >
            Grouped by Error
          </button>
        </div>

        {viewMode === "grouped" ? (
          <ErrorGroupedView
            events={events}
            onResubmitEvent={resubmitSingleEvent}
            onSkipEvent={skipSingleEvent}
            isActionableStatus={isActionableStatus}
          />
        ) : (
          <DataTable
            headCells={headCells}
            headActions={headActions}
            rows={rows}
            withCheckboxes={true}
            noDataMessage="No events available"
            isLoading={isLoading}
            count={rows.length}
            hideDense={true}
            dataRowsPerPage={20}
            onPageChange={nextPage}
            fixedWidth={"-webkit-fill-available"}
          />
        )}
      </div>
    </FilterContext.Provider>
  );
};

export default EventsPanel;
