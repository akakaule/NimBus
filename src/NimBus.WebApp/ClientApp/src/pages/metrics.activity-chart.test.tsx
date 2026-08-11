import type { ReactNode } from "react";
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ActivityChart } from "./metrics";

vi.mock("recharts", () => ({
  CartesianGrid: () => null,
  Line: ({ dataKey }: { dataKey: string }) => (
    <span data-testid="chart-line" data-key={dataKey} />
  ),
  LineChart: ({ children }: { children: ReactNode }) => (
    <div data-testid="line-chart">{children}</div>
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
  it("uses the same line-chart treatment as the event-type chart", () => {
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

    expect(screen.getByTestId("line-chart")).toBeTruthy();
    expect(
      screen
        .getAllByTestId("chart-line")
        .map((line) => line.getAttribute("data-key")),
    ).toEqual(["published", "handled", "failureMarker"]);
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
