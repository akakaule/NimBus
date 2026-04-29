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
import { useUrlFilters } from "hooks/use-url-filters";

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

// URL sentinel meaning "user explicitly wants no status filter". We need this
// because an absent `status` param falls back to defaults (Failed) — so without
// the sentinel, "show all statuses" would silently revert to the default after
// any navigation. Pressing "All statuses" or removing the last chip writes
// `?status=*`; buildEventFilterFromParams translates it back to no filter
// before hitting the API, and the chip combobox treats it as zero chips.
const STATUS_ALL_SENTINEL = "*";

function isAllStatusesSentinel(status: string[]): boolean {
  return status.length === 1 && status[0] === STATUS_ALL_SENTINEL;
}

// URL-driven filter shape. Only the most-visible fields are persisted; advanced
// filters (payload, enqueued, updated) live in the FilterContext as draft state
// only and are picked up at Search-time. The default `status` of "Failed"
// matches the operator UX where the page opens pre-filtered to the most common
// actionable case (also the Reset target). Declared as a closed `type` so it
// satisfies the index-signature constraint of `useUrlFilters<T>`.
type EndpointFilterParams = {
  status: string[];
  eventTypeId: string[];
  eventId: string;
  sessionId: string;
  viewMode: string;
  maxResults: string;
};

const DEFAULT_ENDPOINT_FILTER_PARAMS: EndpointFilterParams = {
  status: [api.ResolutionStatus.Failed],
  eventTypeId: [],
  eventId: "",
  sessionId: "",
  viewMode: "list",
  maxResults: "100",
};

function buildEventFilterFromParams(
  params: EndpointFilterParams,
  endpointId: string,
): api.EventFilter {
  const filter = new api.EventFilter();
  filter.endpointId = endpointId;
  // Sentinel ["*"] means "no status filter" — leave resolutionStatus empty.
  filter.resolutionStatus = isAllStatusesSentinel(params.status)
    ? []
    : (params.status as api.ResolutionStatus[]);
  if (params.eventTypeId.length) filter.eventTypeId = [...params.eventTypeId];
  if (params.eventId) filter.eventId = params.eventId;
  if (params.sessionId) filter.sessionId = params.sessionId;
  return filter;
}

function paramsFromEventFilter(
  filter: api.EventFilter,
  current: EndpointFilterParams,
): EndpointFilterParams {
  // Search committed an empty status array → user means "all statuses". Write
  // the sentinel so the URL preserves intent across navigation.
  const rawStatus = (filter.resolutionStatus ?? []) as string[];
  return {
    status: rawStatus.length === 0 ? [STATUS_ALL_SENTINEL] : rawStatus,
    eventTypeId: (filter.eventTypeId ?? []) as string[],
    eventId: filter.eventId ?? "",
    sessionId: filter.sessionId ?? "",
    viewMode: current.viewMode,
    maxResults: current.maxResults,
  };
}

const EventsPanel = (props: EventsPanelProps) => {
  const client = new api.Client(api.CookieAuth());
  const params = useParams();
  const endpointId = props.endpointId || params.id!;

  const { applied, applyFilters, resetFilters, setFiltersWithoutHistory } =
    useUrlFilters<EndpointFilterParams>(DEFAULT_ENDPOINT_FILTER_PARAMS);

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

  const maxResults = Number(applied.maxResults) || 100;
  const viewMode = (applied.viewMode === "grouped" ? "grouped" : "list") as
    | "list"
    | "grouped";

  // EventFilter (draft) is derived from URL on mount and on URL change. Sub-filters
  // mutate this via the FilterContext as the user types; URL only updates on Search.
  const [eventFilter, setEventFilter] = React.useState<api.EventFilter>(() =>
    buildEventFilterFromParams(applied, endpointId),
  );

  // When the URL changes (Back/forward, Search, Reset, bookmark load) reset the
  // draft filter to match. Sub-filter components are also keyed on URL state so
  // their internal input state re-seeds from the URL-derived initialValue props.
  React.useEffect(() => {
    setEventFilter(buildEventFilterFromParams(applied, endpointId));
  }, [applied, endpointId]);

  const context: IFilterContext = {
    filterContext: eventFilter,
    setProjectContext: setEventFilter,
  };

  // Materialise the default `status=Failed&status=DeadLettered` into the URL on
  // first mount when the URL has no status param. This makes the operator's
  // default landing state explicit in the URL — essential for browser Back from
  // a message-detail page to land back on the *same* filter the user saw.
  React.useEffect(() => {
    const url = new URL(window.location.href);
    if (!url.searchParams.has("status")) {
      setFiltersWithoutHistory(applied);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Re-fetch whenever the applied (URL) filter changes. Covers initial mount,
  // Search, Reset, browser Back/forward, and direct bookmark loads.
  React.useEffect(() => {
    if (!endpointId) return;
    setSessions({});
    fetchEvents(buildEventFilterFromParams(applied, endpointId));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [applied, endpointId]);

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

  // User clicked Search — write the current draft filter to the URL.
  // The useEffect on `applied` then refetches.
  const handleFilterClicked = (filter: api.EventFilter): void => {
    applyFilters(paramsFromEventFilter(filter, applied));
  };

  // Reset — clear all URL filter params back to defaults (which include Failed status).
  const handleReset = (): void => {
    resetFilters();
  };

  // "All statuses" — show every status. Writes the explicit sentinel so the
  // intent survives navigation; an absent param would otherwise fall back to
  // the default (Failed) on the next render.
  const handleClearStatus = (): void => {
    applyFilters({ ...applied, status: [STATUS_ALL_SENTINEL] });
  };

  // Commit-on-change for the Status combobox: chip add/remove writes to the
  // URL immediately so browser Back from an event-detail page restores the
  // exact filter the user had selected, not the page's defaults. Removing the
  // last chip is treated as "All statuses" (sentinel) for the same reason.
  const handleStatusChange = (next: api.ResolutionStatus[]): void => {
    const nextStatus = next.length === 0 ? [STATUS_ALL_SENTINEL] : (next as string[]);
    applyFilters({ ...applied, status: nextStatus });
  };

  const setMaxResults = (next: number): void => {
    applyFilters({ ...applied, maxResults: String(next) });
  };

  const setViewMode = (next: "list" | "grouped"): void => {
    applyFilters({ ...applied, viewMode: next });
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

  // Hide the sentinel from the combobox — "all statuses" should render as an
  // empty chip area, not a chip literally labelled "*".
  const displayedStatuses = isAllStatusesSentinel(applied.status)
    ? []
    : (applied.status as api.ResolutionStatus[]);

  // Key the filter subtree on URL state so sub-filter inputs re-mount and re-seed
  // from `initialValue` props whenever the URL changes (e.g. browser Back/forward).
  const filterRemountKey = `${applied.status.join(",")}|${applied.eventTypeId.join(",")}|${applied.eventId}|${applied.sessionId}`;

  return (
    <FilterContext.Provider value={context}>
      <div className="flex flex-col gap-4">
        <EventFiltering
          key={filterRemountKey}
          handleFilterClicked={handleFilterClicked}
          onReset={handleReset}
          onClearStatus={handleClearStatus}
          onStatusChange={handleStatusChange}
          initialStatuses={displayedStatuses}
          initialEventTypes={applied.eventTypeId}
          initialEventId={applied.eventId}
          initialSessionId={applied.sessionId}
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
