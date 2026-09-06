import { describe, it, expect, afterEach, beforeEach, vi } from "vitest";
import { render, screen, cleanup, fireEvent } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import DataTable from "./data-table-new";
import type { ITableHeadCell, ITableRow } from "./types";

// DataTable uses react-router for row-level navigation when `route` is set,
// so wrap everything in a MemoryRouter. No-op the toast provider since the
// table reads from it for action feedback.
vi.mock("components/ui/toast", () => ({
  useToast: () => ({ addToast: () => {} }),
}));

// Spy on useNavigate so row-click navigation is observable; everything else
// (MemoryRouter included) stays real.
const navigateSpy = vi.fn();
vi.mock("react-router-dom", async (importOriginal) => {
  const actual = await importOriginal<typeof import("react-router-dom")>();
  return { ...actual, useNavigate: () => navigateSpy };
});

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
    const rows = [makeRow("r1", "alpha", 42), makeRow("r2", "beta", 7)];
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

// ---------------------------------------------------------------------------
// Row navigation (ported from DIS 5adc92b7) — plain click navigates in place;
// Ctrl/Cmd-click and middle-click open the row's route in a new tab.
// ---------------------------------------------------------------------------
describe("DataTable row navigation", () => {
  const ROUTE = "/Message/Index/Bob/event-1/0";

  const routedRows: ITableRow[] = [
    {
      id: "row-1",
      route: ROUTE,
      data: new Map([
        ["name", { value: "AliceSaidHello", searchValue: "AliceSaidHello" }],
        ["count", { value: 1, searchValue: 1 }],
      ]),
    },
  ];

  let openedTab: { opener: unknown };

  beforeEach(() => {
    navigateSpy.mockReset();
    openedTab = { opener: {} };
    vi.stubGlobal(
      "open",
      vi.fn(() => openedTab),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function getRow(): HTMLElement {
    renderTable(
      <DataTable headCells={headCells} rows={routedRows} withToolbar={false} />,
    );
    return screen.getByText("AliceSaidHello").closest("tr") as HTMLElement;
  }

  function auxClick(row: HTMLElement, button: number) {
    fireEvent(
      row,
      new MouseEvent("auxclick", { bubbles: true, cancelable: true, button }),
    );
  }

  it("navigates in the same tab on a plain click", () => {
    const row = getRow();

    fireEvent.click(row);

    expect(navigateSpy).toHaveBeenCalledWith(ROUTE);
    expect(window.open).not.toHaveBeenCalled();
  });

  it("opens a new tab on Ctrl+click and nulls the opener", () => {
    const row = getRow();

    fireEvent.click(row, { ctrlKey: true });

    expect(window.open).toHaveBeenCalledWith(ROUTE, "_blank");
    expect(openedTab.opener).toBeNull();
    expect(navigateSpy).not.toHaveBeenCalled();
  });

  it("opens a new tab on Cmd+click (metaKey)", () => {
    const row = getRow();

    fireEvent.click(row, { metaKey: true });

    expect(window.open).toHaveBeenCalledWith(ROUTE, "_blank");
    expect(navigateSpy).not.toHaveBeenCalled();
  });

  it("opens a new tab on middle-click", () => {
    const row = getRow();

    auxClick(row, 1);

    expect(window.open).toHaveBeenCalledWith(ROUTE, "_blank");
    expect(navigateSpy).not.toHaveBeenCalled();
  });

  it("ignores a right-button auxclick (button 2)", () => {
    const row = getRow();

    auxClick(row, 2);

    expect(window.open).not.toHaveBeenCalled();
    expect(navigateSpy).not.toHaveBeenCalled();
  });
});

describe("DataTable row actions menu", () => {
  beforeEach(() => navigateSpy.mockReset());
  const actionRows = (onResubmit: () => boolean): ITableRow[] => [
    {
      ...makeRow("r1", "failed-row", 1),
      route: "/rows/r1",
      bodyActions: [
        {
          name: "Resubmit",
          description: "Send the message again",
          onClick: onResubmit,
        },
        { name: "Skip", onClick: () => false },
      ],
    },
    { ...makeRow("r2", "completed-row", 2), route: "/rows/r2" },
  ];

  it("shows a single ellipsis trigger only on rows that have actions", () => {
    renderTable(
      <DataTable
        headCells={headCells}
        rows={actionRows(() => false)}
        rowActions="menu"
      />,
    );

    expect(screen.getAllByRole("button", { name: "Actions" })).toHaveLength(1);
    expect(screen.queryByRole("button", { name: "Resubmit" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Skip" })).toBeNull();
  });

  it("opens a dropdown listing the actions and runs the chosen one", () => {
    const onResubmit = vi.fn(() => false);
    renderTable(
      <DataTable
        headCells={headCells}
        rows={actionRows(onResubmit)}
        rowActions="menu"
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Actions" }));
    const menu = screen.getByRole("menu");
    expect(screen.queryByRole("dialog")).toBeNull();
    // Compact menu: the description is a tooltip, not inline text.
    expect(menu.textContent).not.toContain("Send the message again");
    expect(
      screen.getByRole("menuitem", { name: "Resubmit" }).getAttribute("title"),
    ).toBe("Send the message again");
    expect(screen.getByRole("menuitem", { name: "Skip" })).toBeDefined();

    fireEvent.click(screen.getByRole("menuitem", { name: /Resubmit/ }));

    expect(onResubmit).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole("menu")).toBeNull();
    // Choosing an action must not also navigate the row.
    expect(navigateSpy).not.toHaveBeenCalled();
  });

  it("keeps inline buttons by default", () => {
    renderTable(
      <DataTable headCells={headCells} rows={actionRows(() => false)} />,
    );

    expect(screen.queryByRole("button", { name: "Actions" })).toBeNull();
    expect(screen.getByRole("button", { name: "Resubmit" })).toBeDefined();
  });
});
