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
  XAxis: () => null,
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
});
