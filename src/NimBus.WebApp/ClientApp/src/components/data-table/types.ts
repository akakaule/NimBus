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
}

export interface ITableHeadCell {
  id: string;
  label: string;
  numeric: boolean;
  width?: number | string;
}

export type SortDirection = "asc" | "desc";
