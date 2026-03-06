import { Button } from "components/ui/button";
import { Input } from "components/ui/input";
import { Select } from "components/ui/select";
import { Tooltip } from "components/ui/tooltip";

export type ViewMode = "cards" | "table";

// Grid view icon
const GridViewIcon = () => (
  <svg className="w-5 h-5" fill="currentColor" viewBox="0 0 24 24">
    <path d="M3 3h8v8H3V3zm0 10h8v8H3v-8zm10-10h8v8h-8V3zm0 10h8v8h-8v-8z" />
  </svg>
);

// Table view icon
const TableRowsIcon = () => (
  <svg className="w-5 h-5" fill="currentColor" viewBox="0 0 24 24">
    <path d="M4 6h16v2H4V6zm0 5h16v2H4v-2zm0 5h16v2H4v-2z" />
  </svg>
);

// Search icon
const SearchIcon = () => (
  <svg
    className="w-4 h-4"
    fill="none"
    stroke="currentColor"
    viewBox="0 0 24 24"
  >
    <path
      strokeLinecap="round"
      strokeLinejoin="round"
      strokeWidth={2}
      d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z"
    />
  </svg>
);

interface IEventTypeSearchToolbarProps {
  searchTerm: string;
  onSearchChange: (value: string) => void;
  selectedNamespace: string;
  onNamespaceChange: (value: string) => void;
  namespaces: string[];
  viewMode: ViewMode;
  onViewModeChange: (mode: ViewMode) => void;
}

const EventTypeSearchToolbar: React.FC<IEventTypeSearchToolbarProps> = ({
  searchTerm,
  onSearchChange,
  selectedNamespace,
  onNamespaceChange,
  namespaces,
  viewMode,
  onViewModeChange,
}) => {
  return (
    <div className="flex flex-wrap gap-4 mb-4 items-center">
      <div className="max-w-[400px] flex-1">
        <Input
          type="text"
          placeholder="Search event types..."
          value={searchTerm}
          onChange={(e) => onSearchChange(e.target.value)}
          leftElement={<SearchIcon />}
        />
      </div>

      <Select
        value={selectedNamespace}
        onChange={(e) => onNamespaceChange(e.target.value)}
        className="max-w-[300px]"
      >
        <option value="">All Namespaces</option>
        {namespaces.map((ns) => (
          <option key={ns} value={ns}>
            {ns}
          </option>
        ))}
      </Select>

      <div className="flex ml-auto border border-input rounded-md">
        <Tooltip content="Card View">
          <Button
            variant={viewMode === "cards" ? "solid" : "ghost"}
            colorScheme={viewMode === "cards" ? "blue" : "gray"}
            onClick={() => onViewModeChange("cards")}
            aria-label="Card view"
            className="rounded-r-none"
          >
            <GridViewIcon />
          </Button>
        </Tooltip>
        <Tooltip content="Table View">
          <Button
            variant={viewMode === "table" ? "solid" : "ghost"}
            colorScheme={viewMode === "table" ? "blue" : "gray"}
            onClick={() => onViewModeChange("table")}
            aria-label="Table view"
            className="rounded-l-none"
          >
            <TableRowsIcon />
          </Button>
        </Tooltip>
      </div>
    </div>
  );
};

export default EventTypeSearchToolbar;
