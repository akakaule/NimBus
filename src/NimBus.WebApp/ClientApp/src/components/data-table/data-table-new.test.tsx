import { describe, it, expect, afterEach, vi } from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import DataTable from "./data-table-new";
import type { ITableHeadCell, ITableRow } from "./types";

// DataTable uses react-router for row-level navigation when `route` is set,
// so wrap everything in a MemoryRouter. No-op the toast provider since the
// table reads from it for action feedback.
vi.mock("components/ui/toast", () => ({
  useToast: () => ({ addToast: () => {} }),
}));

const headCells: ITableHeadCell[] = [
  { id: "name", label: "Name", numeric: false },
  { id: "count", label: "Count", numeric: true },
];

const makeRow = (id: string, name: string, count: number): ITableRow => ({
  id,
  data: new Map([
    ["name", { value: name, searchValue: name }],
    ["count", { value: count, searchValue: count }],
  ]),
});

const renderTable = (ui: React.ReactElement) =>
  render(<MemoryRouter>{ui}</MemoryRouter>);

afterEach(() => cleanup());

describe("DataTable (smoke)", () => {
  it("renders header labels for all configured columns", () => {
    renderTable(<DataTable headCells={headCells} rows={[]} />);
    expect(screen.getByText("Name")).toBeDefined();
    expect(screen.getByText("Count")).toBeDefined();
  });

  it("renders row cell values from the per-row data map", () => {
    const rows = [
      makeRow("r1", "alpha", 42),
      makeRow("r2", "beta", 7),
    ];
    renderTable(<DataTable headCells={headCells} rows={rows} />);

    expect(screen.getByText("alpha")).toBeDefined();
    expect(screen.getByText("beta")).toBeDefined();
    expect(screen.getByText("42")).toBeDefined();
    expect(screen.getByText("7")).toBeDefined();
  });

  it("renders without crashing when rows is empty", () => {
    renderTable(<DataTable headCells={headCells} rows={[]} />);
    // Header still renders; body is empty — the noDataMessage default is
    // "No data" but the exact wording isn't part of this smoke contract.
    expect(screen.getByText("Name")).toBeDefined();
  });
});
