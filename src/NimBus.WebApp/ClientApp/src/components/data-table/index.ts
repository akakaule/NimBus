// Export new TanStack Table implementation
export { DataTable as default, DataTable } from "./data-table-new";

// Export types
export type {
  ITableRow,
  ITableHeadCell,
  ITableHeadAction,
  ITableBodyAction,
  ITableData,
  SortDirection,
} from "./types";

// Re-export ITableHeadCell from header for backward compatibility
export type { ITableHeadCell as TableHeadCell } from "./types";
