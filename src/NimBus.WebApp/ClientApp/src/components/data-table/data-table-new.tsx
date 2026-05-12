import { useState, useMemo, useEffect } from "react";
import { useNavigate } from "react-router-dom";
import {
  useReactTable,
  getCoreRowModel,
  getSortedRowModel,
  getPaginationRowModel,
  getFilteredRowModel,
  flexRender,
  type SortingState,
  type ColumnDef,
  type RowSelectionState,
} from "@tanstack/react-table";
import { cn } from "lib/utils";
import { Spinner } from "components/ui/spinner";
import { Checkbox } from "components/ui/checkbox";
import { Button } from "components/ui/button";
import { Input } from "components/ui/input";
import type {
  ITableRow,
  ITableHeadCell,
  ITableHeadAction,
  SortDirection,
} from "./types";

// Re-export types for backward compatibility
export type {
  ITableRow,
  ITableHeadCell,
  ITableHeadAction,
  ITableBodyAction,
  ITableData,
} from "./types";

interface DataTableProps {
  headCells: ITableHeadCell[];
  headActions?: ITableHeadAction[];
  rows: ITableRow[];
  isLoading?: boolean;
  noDataMessage?: string;
  withToolbar?: boolean;
  withCheckboxes?: boolean;
  order?: SortDirection;
  orderBy?: string;
  styles?: React.CSSProperties;
  count?: number;
  onPageChange?: () => void;
  endpointIds?: string[];
  checkedEndpointIds?: string[];
  checked?: (name: string, state: boolean) => void;
  dataRowsPerPage?: number;
  fixedWidth?: string;
  // Deprecated props (kept for backward compatibility)
  hideDense?: boolean;
  subscribe?: string;
  roleAssignmentScript?: string;
}

export function DataTable({
  headCells,
  headActions,
  rows,
  isLoading = false,
  noDataMessage = "No data available",
  withToolbar = true,
  withCheckboxes = false,
  order = "asc",
  orderBy = "",
  styles,
  count,
  onPageChange,
  endpointIds,
  checkedEndpointIds,
  checked,
  dataRowsPerPage = 20,
  fixedWidth,
}: DataTableProps) {
  const [sorting, setSorting] = useState<SortingState>(
    orderBy ? [{ id: orderBy, desc: order === "desc" }] : [],
  );
  const [rowSelection, setRowSelection] = useState<RowSelectionState>({});
  const [globalFilter, setGlobalFilter] = useState("");

  // Convert Map-based rows to column accessor format
  const columns = useMemo<ColumnDef<ITableRow>[]>(() => {
    const cols: ColumnDef<ITableRow>[] = [];

    // Checkbox column
    if (withCheckboxes) {
      cols.push({
        id: "select",
        header: ({ table }) => (
          <Checkbox
            checked={table.getIsAllPageRowsSelected()}
            indeterminate={table.getIsSomePageRowsSelected()}
            onChange={table.getToggleAllPageRowsSelectedHandler()}
            aria-label="Select all"
          />
        ),
        cell: ({ row }) => (
          <Checkbox
            checked={row.getIsSelected()}
            onChange={row.getToggleSelectedHandler()}
            onClick={(e) => e.stopPropagation()}
            aria-label="Select row"
          />
        ),
        size: 36,
        enableSorting: false,
      });
    }

    // Data columns
    headCells.forEach((cell) => {
      cols.push({
        id: cell.id,
        accessorFn: (row) => row.data.get(cell.id)?.searchValue ?? "",
        header: () => cell.label,
        cell: ({ row }) => {
          const cellData = row.original.data.get(cell.id);
          return (
            <span className="block truncate">{cellData?.value ?? ""}</span>
          );
        },
        size: typeof cell.width === "number" ? cell.width : undefined,
        enableSorting: true,
      });
    });

    // Actions column
    if (rows.some((row) => row.bodyActions && row.bodyActions.length > 0)) {
      cols.push({
        id: "actions",
        header: () =>
          headActions && headActions.length > 0 ? (
            <div className="flex justify-end gap-2">
              {headActions.map((action, i) => (
                <Button
                  key={i}
                  size="xs"
                  variant="outline"
                  disabled={Object.keys(rowSelection).length === 0}
                  onClick={(e) => {
                    e.preventDefault();
                    e.stopPropagation();
                    const selectedRows = rows.filter(
                      (_, idx) => rowSelection[idx],
                    );
                    action.onClick(selectedRows);
                  }}
                >
                  {action.name}
                </Button>
              ))}
            </div>
          ) : null,
        cell: ({ row }) => {
          const actions = row.original.bodyActions;
          if (!actions || actions.length === 0) return null;
          return (
            <div className="flex justify-end gap-2">
              {actions.map((action, i) => (
                <Button
                  key={i}
                  size="xs"
                  variant="outline"
                  onClick={(e) => {
                    e.preventDefault();
                    e.stopPropagation();
                    action.onClick();
                  }}
                >
                  {action.name}
                </Button>
              ))}
            </div>
          );
        },
        enableSorting: false,
      });
    }

    return cols;
  }, [headCells, withCheckboxes, headActions, rows, rowSelection]);

  // Custom global filter function for Map-based data
  const globalFilterFn = useMemo(
    () => (row: ITableRow, filterValue: string) => {
      if (!filterValue) return true;
      const searchLower = filterValue.toLowerCase();
      const data = Array.from(row.data.values());
      return data.some((cell) =>
        cell.searchValue.toString().toLowerCase().includes(searchLower),
      );
    },
    [],
  );

  const table = useReactTable({
    data: rows,
    columns,
    state: {
      sorting,
      rowSelection,
      globalFilter,
    },
    enableRowSelection: withCheckboxes,
    onSortingChange: setSorting,
    onRowSelectionChange: setRowSelection,
    onGlobalFilterChange: setGlobalFilter,
    globalFilterFn: (row, columnId, filterValue) =>
      globalFilterFn(row.original, filterValue),
    getCoreRowModel: getCoreRowModel(),
    getSortedRowModel: getSortedRowModel(),
    getPaginationRowModel: getPaginationRowModel(),
    getFilteredRowModel: getFilteredRowModel(),
    initialState: {
      pagination: {
        pageSize: dataRowsPerPage,
      },
    },
  });

  // Trigger onPageChange when approaching end of data
  useEffect(() => {
    if (onPageChange) {
      const { pageIndex, pageSize } = table.getState().pagination;
      const totalRows = rows.length;
      if (totalRows - pageSize * (pageIndex + 1) <= pageSize) {
        onPageChange();
      }
    }
  }, [table.getState().pagination.pageIndex, onPageChange, rows.length]);

  const selectedRows = useMemo(() => {
    return rows.filter((_, idx) => rowSelection[idx]);
  }, [rows, rowSelection]);

  return (
    <div className="w-full" style={styles}>
      {/* Toolbar */}
      {withToolbar && (
        <div className="flex items-center justify-between px-4 py-3">
          <div className="w-1/2">
            <Input
              type="text"
              placeholder="Search..."
              value={globalFilter}
              onChange={(e) => setGlobalFilter(e.target.value)}
              leftElement={
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
              }
            />
          </div>
          {endpointIds && checked && (
            <EndpointFilter
              endpointIds={endpointIds}
              checkedEndpointIds={checkedEndpointIds}
              checked={checked}
            />
          )}
        </div>
      )}

      {/* Table */}
      <div className="overflow-auto bg-card border border-border rounded-nb-md">
        <table
          className="w-full text-[13px]"
          style={
            fixedWidth ? { tableLayout: "fixed", width: fixedWidth } : undefined
          }
        >
          <thead className="bg-muted sticky top-0 z-10 border-b border-border">
            {table.getHeaderGroups().map((headerGroup) => (
              <tr key={headerGroup.id}>
                {headerGroup.headers.map((header) => (
                  <th
                    key={header.id}
                    className={cn(
                      "px-3.5 py-3 text-left font-semibold text-muted-foreground",
                      "text-[11px] uppercase tracking-[0.06em] whitespace-nowrap",
                      "bg-muted border-b border-border",
                      header.column.getCanSort() &&
                        "cursor-pointer select-none hover:text-foreground",
                    )}
                    style={{
                      width:
                        header.column.id === "select"
                          ? "36px"
                          : header.getSize() !== 150
                            ? header.getSize()
                            : undefined,
                    }}
                    onClick={header.column.getToggleSortingHandler()}
                  >
                    <div className="flex items-center gap-1">
                      {header.isPlaceholder
                        ? null
                        : flexRender(
                            header.column.columnDef.header,
                            header.getContext(),
                          )}
                      {header.column.getCanSort() && (
                        <SortIcon direction={header.column.getIsSorted()} />
                      )}
                    </div>
                  </th>
                ))}
              </tr>
            ))}
          </thead>
          <tbody className="divide-y divide-border">
            {isLoading ? (
              <tr>
                <td colSpan={columns.length} className="py-8">
                  <div className="flex justify-center">
                    <Spinner size="lg" />
                  </div>
                </td>
              </tr>
            ) : table.getRowModel().rows.length === 0 ? (
              <tr>
                <td
                  colSpan={columns.length}
                  className="px-4 py-8 text-muted-foreground"
                >
                  {noDataMessage}
                </td>
              </tr>
            ) : (
              table.getRowModel().rows.map((row) => {
                const rowData = row.original;
                return <TableRow key={row.id} row={row} rowData={rowData} />;
              })
            )}
          </tbody>
        </table>
      </div>

      {/* Pagination */}
      <div className="flex items-center justify-between px-4 py-3 border-t border-border">
        <div className="text-sm text-muted-foreground">
          {table.getFilteredRowModel().rows.length > 0 && (
            <>
              Showing{" "}
              {table.getState().pagination.pageIndex *
                table.getState().pagination.pageSize +
                1}{" "}
              to{" "}
              {Math.min(
                (table.getState().pagination.pageIndex + 1) *
                  table.getState().pagination.pageSize,
                table.getFilteredRowModel().rows.length,
              )}{" "}
              of {count ?? table.getFilteredRowModel().rows.length} results
            </>
          )}
        </div>
        <div className="flex items-center gap-2">
          <Button
            variant="outline"
            size="sm"
            onClick={() => table.previousPage()}
            disabled={!table.getCanPreviousPage()}
          >
            Previous
          </Button>
          <span className="text-sm text-muted-foreground">
            Page {table.getState().pagination.pageIndex + 1} of{" "}
            {table.getPageCount()}
          </span>
          <Button
            variant="outline"
            size="sm"
            onClick={() => table.nextPage()}
            disabled={!table.getCanNextPage()}
          >
            Next
          </Button>
        </div>
      </div>
    </div>
  );
}

// Sort icon component
function SortIcon({ direction }: { direction: false | "asc" | "desc" }) {
  if (!direction) {
    return (
      <svg
        className="w-4 h-4 text-muted-foreground"
        fill="none"
        stroke="currentColor"
        viewBox="0 0 24 24"
      >
        <path
          strokeLinecap="round"
          strokeLinejoin="round"
          strokeWidth={2}
          d="M7 16V4m0 0L3 8m4-4l4 4m6 0v12m0 0l4-4m-4 4l-4-4"
        />
      </svg>
    );
  }
  return (
    <svg
      className={cn("w-4 h-4", direction === "asc" ? "rotate-180" : "")}
      fill="none"
      stroke="currentColor"
      viewBox="0 0 24 24"
    >
      <path
        strokeLinecap="round"
        strokeLinejoin="round"
        strokeWidth={2}
        d="M19 9l-7 7-7-7"
      />
    </svg>
  );
}

// Table row component with proper navigation handling
function TableRow({ row, rowData }: { row: any; rowData: ITableRow }) {
  const navigate = useNavigate();

  return (
    <tr
      className={cn(
        "group hover:bg-primary-tint dark:hover:bg-primary/15 transition-colors",
        row.getIsSelected() && "bg-primary-50 dark:bg-primary/10",
        rowData.route && "cursor-pointer",
      )}
      onClick={
        rowData.route
          ? (e: React.MouseEvent) => {
              // Don't navigate if clicking interactive elements
              const target = e.target as HTMLElement;
              if (
                target.closest(
                  'input, button, a, [role="button"], [role="checkbox"]',
                )
              ) {
                return;
              }
              navigate(rowData.route!);
            }
          : undefined
      }
      title={rowData.hoverText}
    >
      {row.getVisibleCells().map((cell: any) => (
        <td
          key={cell.id}
          className={cn(
            "px-3.5 py-2.5 border-b border-border",
            "text-foreground tabular-nums",
            cell.column.id === "select" && "w-9",
          )}
        >
          {flexRender(cell.column.columnDef.cell, cell.getContext())}
        </td>
      ))}
    </tr>
  );
}

// Endpoint filter component (simplified)
function EndpointFilter({
  endpointIds,
  checkedEndpointIds,
  checked,
}: {
  endpointIds: string[];
  checkedEndpointIds?: string[];
  checked: (name: string, state: boolean) => void;
}) {
  const [isOpen, setIsOpen] = useState(false);

  return (
    <div className="relative">
      <Button variant="outline" size="sm" onClick={() => setIsOpen(!isOpen)}>
        Filter Endpoints
        <svg
          className="w-4 h-4 ml-1"
          fill="none"
          stroke="currentColor"
          viewBox="0 0 24 24"
        >
          <path
            strokeLinecap="round"
            strokeLinejoin="round"
            strokeWidth={2}
            d="M19 9l-7 7-7-7"
          />
        </svg>
      </Button>
      {isOpen && (
        <div className="absolute right-0 mt-2 w-64 bg-popover text-popover-foreground rounded-lg shadow-lg border border-border z-20 max-h-64 overflow-auto">
          <div className="p-2">
            {endpointIds.map((id) => (
              <label
                key={id}
                className="flex items-center gap-2 px-2 py-1 hover:bg-accent rounded cursor-pointer"
              >
                <input
                  type="checkbox"
                  checked={checkedEndpointIds?.includes(id) ?? false}
                  onChange={(e) => checked(id, e.target.checked)}
                  className="rounded border-input text-primary focus:ring-primary"
                />
                <span className="text-sm truncate">{id}</span>
              </label>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

// Default export for backward compatibility
export default DataTable;
