import type { ComponentProps } from "react";
import { describe, it, expect, afterEach } from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import * as api from "api-client";
import { ThemeProvider } from "hooks/use-theme";
import ByEventTypeTab, {
  MAX_CHART_SERIES,
  buildBucketGrid,
  buildChartRows,
  buildTableRows,
  formatBucketLabel,
  pickVisibleSeries,
  spansMoreThanOneDay,
} from "./by-event-type-tab";

function series(
  eventTypeId: string,
  points: [string, number][],
): api.EventTypeSeries {
  return {
    eventTypeId,
    total: points.reduce((s, [, n]) => s + n, 0),
    dataPoints: points.map(([timestamp, published]) => ({
      timestamp,
      published,
    })),
  } as api.EventTypeSeries;
}

afterEach(cleanup);

describe("pickVisibleSeries", () => {
  const all = ["A", "B", "C", "D", "E", "F", "G"].map((id, i) =>
    series(id, [["2026-08-10T10", 100 - i]]),
  );

  it("defaults to the top N by server order", () => {
    const visible = pickVisibleSeries(all, []);
    expect(visible.map((s) => s.eventTypeId)).toEqual([
      "A",
      "B",
      "C",
      "D",
      "E",
    ]);
    expect(visible).toHaveLength(MAX_CHART_SERIES);
  });

  it("shows the selection instead when one is made", () => {
    const visible = pickVisibleSeries(all, ["F", "B"]);
    expect(visible.map((s) => s.eventTypeId)).toEqual(["B", "F"]);
  });

  it("caps an oversized selection at the palette size", () => {
    const visible = pickVisibleSeries(all, ["A", "B", "C", "D", "E", "F"]);
    expect(visible).toHaveLength(MAX_CHART_SERIES);
  });
});

describe("spansMoreThanOneDay", () => {
  it("is false for a window of 24 hours or less", () => {
    expect(spansMoreThanOneDay(["2026-08-09T10", "2026-08-10T10"])).toBe(false);
    expect(spansMoreThanOneDay(["2026-08-10T00", "2026-08-10T23"])).toBe(false);
  });

  it("is true when the window exceeds one day", () => {
    expect(spansMoreThanOneDay(["2026-08-08T10", "2026-08-10T11"])).toBe(true);
  });

  it("is false for empty or single-bucket windows", () => {
    expect(spansMoreThanOneDay([])).toBe(false);
    expect(spansMoreThanOneDay(["2026-08-10T10"])).toBe(false);
    expect(spansMoreThanOneDay([undefined, "2026-08-10T10"])).toBe(false);
  });
});

describe("formatBucketLabel", () => {
  it("keeps plain hour labels inside a single day", () => {
    expect(formatBucketLabel("2026-08-10T14", "hour")).toMatch(/^\d{2}:00$/);
  });

  it("carries the date on hour labels when asked", () => {
    expect(formatBucketLabel("2026-08-10T14", "hour", true)).toMatch(
      /^\d{2}\/\d{2} \d{2}:00$/,
    );
  });

  it("leaves day and minute labels unchanged by the flag", () => {
    expect(formatBucketLabel("2026-08-10", "day", true)).toMatch(
      /^\d{2}\/\d{2}$/,
    );
    expect(formatBucketLabel("2026-08-10T14:30", "minute", true)).toMatch(
      /^\d{2}:\d{2}$/,
    );
  });
});

describe("buildBucketGrid", () => {
  it("fills hour gaps between the observed keys", () => {
    expect(
      buildBucketGrid(["2026-08-10T10", "2026-08-10T13"], "hour"),
    ).toEqual([
      "2026-08-10T10",
      "2026-08-10T11",
      "2026-08-10T12",
      "2026-08-10T13",
    ]);
  });

  it("steps days across month boundaries", () => {
    expect(buildBucketGrid(["2026-07-30", "2026-08-02"], "day")).toEqual([
      "2026-07-30",
      "2026-07-31",
      "2026-08-01",
      "2026-08-02",
    ]);
  });

  it("returns a single key unchanged", () => {
    expect(buildBucketGrid(["2026-08-10T10:05"], "minute")).toEqual([
      "2026-08-10T10:05",
    ]);
  });
});

describe("buildChartRows", () => {
  it("zero-fills the sparse buckets per series", () => {
    const rows = buildChartRows(
      [
        series("A", [
          ["2026-08-10T10", 5],
          ["2026-08-10T12", 2],
        ]),
        series("B", [["2026-08-10T11", 7]]),
      ],
      "hour",
    );
    expect(rows).toEqual([
      { ts: "2026-08-10T10", A: 5, B: 0 },
      { ts: "2026-08-10T11", A: 0, B: 7 },
      { ts: "2026-08-10T12", A: 2, B: 0 },
    ]);
  });
});

describe("buildTableRows", () => {
  it("joins handled/failed from the overview and computes the share", () => {
    const overview = {
      published: [],
      handled: [
        { endpointId: "e1", eventTypeId: "A", count: 3 },
        { endpointId: "e2", eventTypeId: "A", count: 2 },
      ],
      failed: [{ endpointId: "e1", eventTypeId: "B", count: 1 }],
    } as unknown as api.MetricsOverview;

    const rows = buildTableRows(
      [series("A", [["2026-08-10T10", 6]]), series("B", [["2026-08-10T10", 2]])],
      overview,
    );

    expect(rows).toEqual([
      { eventTypeId: "A", published: 6, handled: 5, failed: 0, share: 0.75 },
      { eventTypeId: "B", published: 2, handled: 0, failed: 1, share: 0.25 },
    ]);
  });
});

describe("ByEventTypeTab", () => {
  const data = {
    bucketSize: "hour",
    series: [
      series("EmployeeHiredEvent", [["2026-08-10T10", 10]]),
      series("EmployeeStoppedEvent", [["2026-08-10T10", 4]]),
    ],
  } as unknown as api.EventTypeTimeSeriesOverview;

  function renderTab(
    props: Partial<ComponentProps<typeof ByEventTypeTab>> = {},
  ) {
    return render(
      <ThemeProvider>
        <ByEventTypeTab
          data={data}
          overview={null}
          periodLabel="1d"
          loading={false}
          selectedTypes={[]}
          onSelectedTypesChange={() => {}}
          {...props}
        />
      </ThemeProvider>,
    );
  }

  it("lists every event type in the table", () => {
    renderTab();
    expect(screen.getAllByText("EmployeeHiredEvent").length).toBeGreaterThan(0);
    expect(
      screen.getAllByText("EmployeeStoppedEvent").length,
    ).toBeGreaterThan(0);
  });

  it("search narrows the table rows", async () => {
    renderTab();
    await userEvent.type(
      screen.getByLabelText(/search event types/i),
      "Stopped",
    );
    expect(screen.getByText("1 of 2 · 1d")).toBeTruthy();
  });

  it("a selection filters the table to the selected types", () => {
    renderTab({ selectedTypes: ["EmployeeStoppedEvent"] });
    expect(screen.getByText("1 of 2 · 1d")).toBeTruthy();
  });

  it("shows the empty state when there is no traffic", () => {
    renderTab({
      data: {
        bucketSize: "hour",
        series: [],
      } as unknown as api.EventTypeTimeSeriesOverview,
    });
    expect(
      screen.getByText(/no published messages in this window/i),
    ).toBeTruthy();
  });
});
