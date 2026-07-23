import type { ReactNode } from "react";

export interface ITableData {
  value: ReactNode;
  searchValue: string | number;
}

export interface ITableBodyAction {
  name: string;
  onClick: () => boolean;
}

export interface ITableHeadAction {
  name: string;
  onClick: (selectedRows: ITableRow[]) => boolean;
}

export interface ITableRow {
  id: string;
  route?: string;
  data: Map<string, ITableData>;
  bodyActions?: ITableBodyAction[];
  hoverText?: string;
  // Optional row tint. "reported" gives reported events a subtle green
  // background so they stand out at a glance.
  tone?: "reported";
}

export interface ITableHeadCell {
  id: string;
  label: string;
  numeric: boolean;
  width?: number | string;
  // Optional help text shown via an info icon next to the column label.
  info?: string;
}

export type SortDirection = "asc" | "desc";
