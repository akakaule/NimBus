import { useEffect, useMemo, useState } from "react";
import * as api from "api-client";
import Page from "components/page";
import { Spinner } from "components/ui/spinner";
import { Badge } from "components/ui/badge";
import { NamespacePill } from "components/ui/namespace-pill";
import { EmptyState } from "components/ui/empty-state";
import DataTable, { ITableRow, ITableHeadCell } from "components/data-table";
import EventTypeSearchToolbar, {
  ViewMode,
} from "components/event-types/event-type-search-toolbar";
import EventTypeNamespaceGroup, {
  INamespaceGroup,
  EventTypeWithCounts,
} from "components/event-types/event-type-namespace-group";
import { useUrlFilters } from "hooks/use-url-filters";

enum TableColumns {
  name = "name",
  namespace = "namespace",
  description = "description",
  producers = "producers",
  consumers = "consumers",
}

// URL-driven filter shape. searchTerm + selectedNamespace + viewMode are all stored
// in query params so that pressing Back (e.g. after drilling into an event type)
// restores the same filter state. Declared as a closed `type` so it satisfies the
// index-signature constraint of `useUrlFilters<T>`.
type EventTypesFilter = {
  searchTerm: string;
  selectedNamespace: string;
  viewMode: string;
};

const DEFAULT_EVENT_TYPES_FILTER: EventTypesFilter = {
  searchTerm: "",
  selectedNamespace: "",
  viewMode: "cards",
};

const EventTypesList: React.FC = () => {
  const { applied, setFiltersWithoutHistory } =
    useUrlFilters<EventTypesFilter>(DEFAULT_EVENT_TYPES_FILTER);

  const searchTerm = applied.searchTerm;
  const selectedNamespace = applied.selectedNamespace;
  const viewMode = (applied.viewMode === "table" ? "table" : "cards") as ViewMode;

  const [eventTypes, setEventTypes] = useState<api.EventType[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const fetchData = async () => {
      const client = new api.Client(api.CookieAuth());
      try {
        const types = await client.getEventTypes();
        setEventTypes(types.filter((et) => et.id));
      } catch (error) {
        console.error("Failed to fetch event types:", error);
      } finally {
        setLoading(false);
      }
    };
    fetchData();
  }, []);

  const UNCATEGORIZED = "Uncategorized";

  const namespaces = useMemo(() => {
    const nsSet = new Set<string>();
    let hasUncategorized = false;
    eventTypes.forEach((et) => {
      if (et.namespace) {
        nsSet.add(et.namespace);
      } else {
        hasUncategorized = true;
      }
    });
    const sorted = Array.from(nsSet).sort();
    if (hasUncategorized) {
      sorted.push(UNCATEGORIZED);
    }
    return sorted;
  }, [eventTypes]);

  const filteredEventTypes = useMemo(() => {
    return eventTypes.filter((et) => {
      const matchesSearch =
        !searchTerm ||
        et.name?.toLowerCase().includes(searchTerm.toLowerCase()) ||
        et.namespace?.toLowerCase().includes(searchTerm.toLowerCase()) ||
        et.description?.toLowerCase().includes(searchTerm.toLowerCase());

      const matchesNamespace =
        !selectedNamespace ||
        et.namespace === selectedNamespace ||
        (selectedNamespace === UNCATEGORIZED && !et.namespace);

      return matchesSearch && matchesNamespace;
    });
  }, [eventTypes, searchTerm, selectedNamespace]);

  const groupedEventTypes = useMemo((): INamespaceGroup[] => {
    const groups: { [ns: string]: EventTypeWithCounts[] } = {};

    filteredEventTypes.forEach((et) => {
      const ns = et.namespace || UNCATEGORIZED;
      if (!groups[ns]) {
        groups[ns] = [];
      }
      const eventTypeWithCounts: EventTypeWithCounts = {
        eventType: et,
        producerCount: et.producerCount ?? 0,
        consumerCount: et.consumerCount ?? 0,
      };
      groups[ns].push(eventTypeWithCounts);
    });

    return Object.entries(groups)
      .map(([namespace, eventTypes]) => ({
        namespace,
        eventTypes: eventTypes.sort((a, b) =>
          (a.eventType.name || "").localeCompare(b.eventType.name || ""),
        ),
      }))
      .sort((a, b) => a.namespace.localeCompare(b.namespace));
  }, [filteredEventTypes]);

  const tableRows = useMemo((): ITableRow[] => {
    return filteredEventTypes.map((et) => ({
      id: et.id,
      route: `/EventTypes/Details/${et.id}`,
      data: new Map([
        [
          TableColumns.name,
          {
            value: (
              <span className="text-status-info dark:text-blue-300 font-bold">
                {et.name}
              </span>
            ),
            searchValue: et.name || "",
          },
        ],
        [
          TableColumns.namespace,
          {
            value: (
              <NamespacePill size="sm">
                {et.namespace || UNCATEGORIZED}
              </NamespacePill>
            ),
            searchValue: et.namespace || UNCATEGORIZED,
          },
        ],
        [
          TableColumns.description,
          {
            value: (
              <span className="text-sm line-clamp-2">
                {et.description || "No description"}
              </span>
            ),
            searchValue: et.description || "",
          },
        ],
        [
          TableColumns.producers,
          {
            value: <Badge variant="success">{et.producerCount ?? 0}</Badge>,
            searchValue: String(et.producerCount ?? 0),
          },
        ],
        [
          TableColumns.consumers,
          {
            value: <Badge variant="info">{et.consumerCount ?? 0}</Badge>,
            searchValue: String(et.consumerCount ?? 0),
          },
        ],
      ]),
    }));
  }, [filteredEventTypes]);

  const headCells: ITableHeadCell[] = [
    { id: TableColumns.name, label: "Name", numeric: false },
    { id: TableColumns.namespace, label: "Namespace", numeric: false },
    { id: TableColumns.description, label: "Description", numeric: false },
    { id: TableColumns.producers, label: "Producers", numeric: true },
    { id: TableColumns.consumers, label: "Consumers", numeric: true },
  ];

  const eventTypeCount = eventTypes.length;
  const namespaceCount = namespaces.length;

  if (loading) {
    return (
      <Page
        title="Event Types"
        subtitle="Contracts published across the bus"
      >
        <div className="flex items-center justify-center w-full h-[200px]">
          <Spinner size="xl" color="primary" />
        </div>
      </Page>
    );
  }

  // Live-filter inputs (no Search button): every change replaces the current URL
  // entry rather than adding a new one, so history isn't polluted with one entry
  // per keystroke. Browser Back from a detail page still returns to the URL with
  // filters applied.
  const setSearchTerm = (next: string) =>
    setFiltersWithoutHistory({ ...applied, searchTerm: next });
  const setSelectedNamespace = (next: string) =>
    setFiltersWithoutHistory({ ...applied, selectedNamespace: next });
  const setViewMode = (next: ViewMode) =>
    setFiltersWithoutHistory({ ...applied, viewMode: next });

  const hasActiveFilters = searchTerm.length > 0 || selectedNamespace.length > 0;
  const subtitle = `${eventTypeCount} contract${eventTypeCount === 1 ? "" : "s"} across ${namespaceCount} namespace${namespaceCount === 1 ? "" : "s"}`;

  return (
    <Page title="Event Types" subtitle={subtitle}>
      <div className="w-full">
        <EventTypeSearchToolbar
          searchTerm={searchTerm}
          onSearchChange={setSearchTerm}
          selectedNamespace={selectedNamespace}
          onNamespaceChange={setSelectedNamespace}
          namespaces={namespaces}
          viewMode={viewMode}
          onViewModeChange={setViewMode}
        />

        {filteredEventTypes.length === 0 ? (
          <EmptyState
            icon="◌"
            title={
              hasActiveFilters
                ? "No event types match your filters"
                : "No event types registered yet"
            }
            description={
              hasActiveFilters
                ? "Try a different search term or clear the namespace filter."
                : "Event types will appear here once endpoints declare their published and consumed contracts."
            }
            action={
              hasActiveFilters && (
                <button
                  type="button"
                  onClick={() => {
                    setSearchTerm("");
                    setSelectedNamespace("");
                  }}
                  className="text-primary-600 hover:text-primary text-[13px] font-semibold underline-offset-2 hover:underline"
                >
                  Clear all filters
                </button>
              )
            }
          />
        ) : viewMode === "cards" ? (
          <EventTypeNamespaceGroup groups={groupedEventTypes} />
        ) : (
          <DataTable
            headCells={headCells}
            rows={tableRows}
            noDataMessage="No event types available"
            isLoading={false}
            orderBy={TableColumns.name}
            dataRowsPerPage={20}
            count={tableRows.length}
            withToolbar={false}
          />
        )}
      </div>
    </Page>
  );
};

export default EventTypesList;
