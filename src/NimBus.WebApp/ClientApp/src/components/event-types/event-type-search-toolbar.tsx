import { Select } from "components/ui/select";
import { FilterSearch } from "components/ui/filter-toolbar";

interface IEventTypeSearchToolbarProps {
  searchTerm: string;
  onSearchChange: (value: string) => void;
  selectedNamespace: string;
  onNamespaceChange: (value: string) => void;
  namespaces: string[];
  selectedEndpoint: string;
  onEndpointChange: (value: string) => void;
  endpoints: string[];
}

const EventTypeSearchToolbar: React.FC<IEventTypeSearchToolbarProps> = ({
  searchTerm,
  onSearchChange,
  selectedNamespace,
  onNamespaceChange,
  namespaces,
  selectedEndpoint,
  onEndpointChange,
  endpoints,
}) => {
  return (
    <div className="mb-4 flex flex-wrap items-center gap-3">
      <FilterSearch
        value={searchTerm}
        onChange={onSearchChange}
        placeholder="Search event types…"
        className="w-full sm:w-[320px]"
      />
      <Select
        value={selectedNamespace}
        onChange={(e) => onNamespaceChange(e.target.value)}
        className="w-full sm:w-[240px]"
        aria-label="Filter by namespace"
      >
        <option value="">All Namespaces</option>
        {namespaces.map((ns) => (
          <option key={ns} value={ns}>
            {ns}
          </option>
        ))}
      </Select>
      <Select
        value={selectedEndpoint}
        onChange={(e) => onEndpointChange(e.target.value)}
        className="w-full sm:w-[220px]"
        aria-label="Filter by endpoint"
      >
        <option value="">All Endpoints</option>
        {endpoints.map((ep) => (
          <option key={ep} value={ep}>
            {ep}
          </option>
        ))}
      </Select>
    </div>
  );
};

export default EventTypeSearchToolbar;
