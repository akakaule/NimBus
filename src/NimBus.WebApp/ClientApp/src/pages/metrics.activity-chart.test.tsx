import type { ReactNode } from "react";
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ActivityChart } from "./metrics";

vi.mock("recharts", () => ({
  CartesianGrid: () => null,
  Bar: ({ dataKey, stackId }: { dataKey: string; stackId?: string }) => (
    <span data-testid="chart-bar" data-key={dataKey} data-stack={stackId} />
  ),
  BarChart: ({
    children,
    data,
  }: {
    children: ReactNode;
    data: { timestamp: string; published: number }[];
  }) => (
    <div data-testid="bar-chart" data-rows={JSON.stringify(data)}>
      {children}
    </div>
  ),
  ResponsiveContainer: ({ children }: { children: ReactNode }) => (
    <div>{children}</div>
  ),
  Tooltip: () => null,
  XAxis: ({
    tickFormatter,
  }: {
    tickFormatter?: (timestamp: string) => string;
  }) => (
    <span
      data-testid="x-axis"
      data-sample-tick={tickFormatter?.("2026-08-10T14")}
    />
  ),
  YAxis: () => null,
}));

afterEach(cleanup);

describe("ActivityChart", () => {
  it("draws published beside a handled + failed stack", () => {
    render(
      <ActivityChart
        bucketSize="hour"
        dataPoints={[
          {
            timestamp: "2026-08-10T10",
            published: 5,
            handled: 4,
            failed: 1,
          },
        ]}
      />,
    );

    expect(screen.getByTestId("bar-chart")).toBeTruthy();
    expect(
      screen
        .getAllByTestId("chart-bar")
        .map(
          (bar) =>
            `${bar.getAttribute("data-key")}:${bar.getAttribute("data-stack") ?? ""}`,
        ),
    ).toEqual(["published:published", "handled:outcome", "failed:outcome"]);
  });

  it("zero-fills buckets missing between observed ones", () => {
    render(
      <ActivityChart
        bucketSize="hour"
        dataPoints={[
          { timestamp: "2026-08-10T10", published: 5, handled: 4, failed: 1 },
          { timestamp: "2026-08-10T13", published: 2, handled: 2, failed: 0 },
        ]}
      />,
    );

    const rows = JSON.parse(
      screen.getByTestId("bar-chart").getAttribute("data-rows") ?? "[]",
    ) as { timestamp: string; published: number }[];
    expect(rows.map((r) => [r.timestamp, r.published])).toEqual([
      ["2026-08-10T10", 5],
      ["2026-08-10T11", 0],
      ["2026-08-10T12", 0],
      ["2026-08-10T13", 2],
    ]);
  });

  it("keeps the empty state when no activity is available", () => {
    render(<ActivityChart bucketSize="hour" dataPoints={[]} />);

    expect(screen.getByText(/no activity in this window/i)).toBeTruthy();
  });

  it("keeps plain hour ticks when the window fits in one day", () => {
    render(
      <ActivityChart
        bucketSize="hour"
        dataPoints={[
          { timestamp: "2026-08-10T02", published: 1, handled: 1, failed: 0 },
          { timestamp: "2026-08-10T14", published: 2, handled: 2, failed: 0 },
        ]}
      />,
    );

    expect(
      screen.getByTestId("x-axis").getAttribute("data-sample-tick"),
    ).toMatch(/^\d{2}:00$/);
  });

  it("adds the date to hour ticks when the window spans more than one day", () => {
    render(
      <ActivityChart
        bucketSize="hour"
        dataPoints={[
          { timestamp: "2026-08-07T10", published: 1, handled: 1, failed: 0 },
          { timestamp: "2026-08-10T14", published: 2, handled: 2, failed: 0 },
        ]}
      />,
    );

    expect(
      screen.getByTestId("x-axis").getAttribute("data-sample-tick"),
    ).toMatch(/^\d{2}\/\d{2} \d{2}:00$/);
  });
});
