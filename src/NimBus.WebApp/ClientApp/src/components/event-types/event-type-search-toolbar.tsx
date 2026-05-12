import { Select } from "components/ui/select";
import { FilterToolbar, FilterSearch } from "components/ui/filter-toolbar";
import { cn } from "lib/utils";

export type ViewMode = "cards" | "table";

const GridViewIcon = () => (
  <svg className="w-4 h-4" fill="currentColor" viewBox="0 0 24 24">
    <path d="M3 3h8v8H3V3zm0 10h8v8H3v-8zm10-10h8v8h-8V3zm0 10h8v8h-8v-8z" />
  </svg>
);

const TableRowsIcon = () => (
  <svg className="w-4 h-4" fill="currentColor" viewBox="0 0 24 24">
    <path d="M4 6h16v2H4V6zm0 5h16v2H4v-2zm0 5h16v2H4v-2z" />
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

const segBtn = (active: boolean) =>
  cn(
    "px-2.5 py-1.5 rounded-md text-xs font-semibold transition-colors",
    "inline-flex items-center gap-1.5",
    active
      ? "bg-primary text-white"
      : "text-muted-foreground hover:text-foreground",
  );

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
    <FilterToolbar
      className="mb-4"
      search={
        <FilterSearch
          value={searchTerm}
          onChange={onSearchChange}
          placeholder="Search event types…"
        />
      }
      actions={
        <Select
          value={selectedNamespace}
          onChange={(e) => onNamespaceChange(e.target.value)}
          className="min-w-[200px] max-w-[300px]"
        >
          <option value="">All Namespaces</option>
          {namespaces.map((ns) => (
            <option key={ns} value={ns}>
              {ns}
            </option>
          ))}
        </Select>
      }
      trailing={
        <div
          className={cn(
            "inline-flex items-center bg-card border border-border",
            "rounded-nb-md p-[3px] gap-[2px]",
          )}
        >
          <button
            type="button"
            onClick={() => onViewModeChange("cards")}
            className={segBtn(viewMode === "cards")}
            aria-label="Card view"
          >
            <GridViewIcon /> Grid
          </button>
          <button
            type="button"
            onClick={() => onViewModeChange("table")}
            className={segBtn(viewMode === "table")}
            aria-label="Table view"
          >
            <TableRowsIcon /> Table
          </button>
        </div>
      }
    />
  );
};

export default EventTypeSearchToolbar;
