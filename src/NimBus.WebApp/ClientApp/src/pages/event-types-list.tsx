import { useState, useEffect, useMemo } from "react";
import * as api from "api-client";
import Page from "components/page";
import { Spinner } from "components/ui/spinner";
import { Badge } from "components/ui/badge";
import DataTable, { ITableRow, ITableHeadCell } from "components/data-table";
import EventTypeSearchToolbar, {
  ViewMode,
} from "components/event-types/event-type-search-toolbar";
import EventTypeNamespaceGroup, {
  INamespaceGroup,
  EventTypeWithCounts,
} from "components/event-types/event-type-namespace-group";

enum TableColumns {
  name = "name",
  namespace = "namespace",
  description = "description",
  producers = "producers",
  consumers = "consumers",
}

const EventTypesList: React.FC = () => {
  const [eventTypes, setEventTypes] = useState<api.EventType[]>([]);
  const [loading, setLoading] = useState(true);
  const [searchTerm, setSearchTerm] = useState("");
  const [selectedNamespace, setSelectedNamespace] = useState("");
  const [viewMode, setViewMode] = useState<ViewMode>("cards");

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
            value: <span className="text-blue-600 font-bold">{et.name}</span>,
            searchValue: et.name || "",
          },
        ],
        [
          TableColumns.namespace,
          {
            value: (
              <Badge
                variant={et.namespace ? "primary" : "default"}
                className={et.namespace ? "bg-purple-100 text-purple-800" : ""}
              >
                {et.namespace || UNCATEGORIZED}
              </Badge>
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

  if (loading) {
    return (
      <Page title="Event Types">
        <div className="flex items-center justify-center w-full h-[200px]">
          <Spinner size="xl" color="primary" />
        </div>
      </Page>
    );
  }

  return (
    <Page title="Event Types">
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
          <div className="flex items-center justify-center h-[200px]">
            <p className="text-muted-foreground">
              No event types found matching your criteria.
            </p>
          </div>
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
