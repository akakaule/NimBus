import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { Moment } from "moment";
import * as api from "api-client";
import Metrics from "./metrics";

const mocks = vi.hoisted(() => ({
  overview: vi.fn(),
  latency: vi.fn(),
  timeseries: vi.fn(),
  byEventType: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual =
    await vi.importActual<typeof import("api-client")>("api-client");
  class FakeClient {
    getMetricsOverview = mocks.overview;
    getMetricsLatency = mocks.latency;
    getMetricsTimeseries = mocks.timeseries;
    getMetricsTimeseriesByEventtype = mocks.byEventType;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

// recharts needs layout jsdom doesn't have; the charts aren't under test here.
vi.mock("recharts", () => {
  const Pass = ({ children }: { children?: ReactNode }) => <>{children}</>;
  const Nothing = () => null;
  return {
    Bar: Nothing,
    BarChart: Pass,
    CartesianGrid: Nothing,
    Legend: Nothing,
    Line: Nothing,
    LineChart: Pass,
    ResponsiveContainer: Pass,
    Tooltip: Nothing,
    XAxis: Nothing,
    YAxis: Nothing,
  };
});

// The By event type tab reads the theme; its own tests cover it.
vi.mock("components/metrics/by-event-type-tab", async () => ({
  ...(await vi.importActual<
    typeof import("components/metrics/by-event-type-tab")
  >("components/metrics/by-event-type-tab")),
  default: () => null,
}));

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function resolveAll() {
  mocks.overview.mockResolvedValue(new api.MetricsOverview());
  mocks.latency.mockResolvedValue(null);
  mocks.timeseries.mockResolvedValue(null);
  mocks.byEventType.mockResolvedValue(null);
}

const iso = (m: Moment | undefined) => m?.toISOString();

describe("Metrics page time range", () => {
  it("loads every card for a custom range, and a preset returns to the period", async () => {
    resolveAll();
    render(
      <MemoryRouter>
        <Metrics />
      </MemoryRouter>,
    );
    await waitFor(() => expect(mocks.overview).toHaveBeenCalledTimes(1));
    expect(mocks.overview).toHaveBeenLastCalledWith(
      api.Period._1d,
      undefined,
      undefined,
    );

    fireEvent.click(screen.getByRole("button", { name: "Custom…" }));
    fireEvent.change(screen.getByLabelText("Start time (local)"), {
      target: { value: "2026-09-20T08:00" },
    });
    fireEvent.change(screen.getByLabelText("End time (local)"), {
      target: { value: "2026-09-21T17:30" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    await waitFor(() => expect(mocks.overview).toHaveBeenCalledTimes(2));
    const from = new Date(2026, 8, 20, 8, 0).toISOString();
    const to = new Date(2026, 8, 21, 17, 30).toISOString();
    for (const call of [
      mocks.overview,
      mocks.latency,
      mocks.timeseries,
      mocks.byEventType,
    ]) {
      const [, f, t] = call.mock.lastCall as [api.Period, Moment, Moment];
      expect([iso(f), iso(t)]).toEqual([from, to]);
    }
    expect(screen.getByText("20/09 08:00–21/09 17:30")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "7d" }));
    await waitFor(() => expect(mocks.overview).toHaveBeenCalledTimes(3));
    expect(mocks.overview).toHaveBeenLastCalledWith(
      api.Period._7d,
      undefined,
      undefined,
    );
    expect(screen.getByRole("button", { name: "Custom…" })).toBeTruthy();
  });
});
