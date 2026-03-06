import { ReactNode } from "react";

export interface NavigationItem {
  name: string;
  path: string;
  header: boolean;
  render?: () => ReactNode;
}

export type Navigation = NavigationItem[];
