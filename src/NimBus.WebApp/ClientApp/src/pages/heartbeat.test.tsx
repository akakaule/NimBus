import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import moment from "moment";
import Heartbeat from "./heartbeat";

const mocks = vi.hoisted(() => ({
  getHeartbeatPage: vi.fn(),
  subscribeHeartbeatUpdates: vi.fn(),
  heartbeatUpdate: undefined as (() => void) | undefined,
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") =
    await vi.importActual("api-client");
  class FakeClient {
    getHeartbeatPage = mocks.getHeartbeatPage;
  }

  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

vi.mock("lib/grid-events-connection", () => ({
  subscribeHeartbeatUpdates: mocks.subscribeHeartbeatUpdates,
}));

function page(overrides: Record<string, unknown> = {}) {
  return {
    windowDays: 30,
    adaptersTotal: 2,
    adaptersReporting: 1,
    fleetUptime: 0.975,
    missedBeatsToday: 3,
    longestGap: 5400,
    adaptersNeedingAttention: ["erp"],
    adapters: [
      {
        endpointId: "crm",
        status: "Unsupported",
        liveness: "alive",
        uptime: 0.95,
        days: [
          {
            dayUtc: moment.utc("2026-08-16T00:00:00Z"),
            state: "partial",
            expected: 20,
            received: 19,
            missed: 1,
            coverage: 0.5,
          },
        ],
      },
    ],
    gaps: [
      {
        endpointId: "erp",
        fromUtc: moment.utc("2026-08-16T08:00:00Z"),
        durationSeconds: 5400,
        ongoing: true,
        cause: "No response",
      },
    ],
    ...overrides,
  };
}

beforeEach(() => {
  mocks.getHeartbeatPage.mockReset().mockResolvedValue(page());
  mocks.heartbeatUpdate = undefined;
  mocks.subscribeHeartbeatUpdates
    .mockReset()
    .mockImplementation((onUpdate: () => void) => {
      mocks.heartbeatUpdate = onUpdate;
      return { dispose: vi.fn() };
    });
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("Heartbeat page", () => {
  it("renders fleet history without treating an unsupported responder as offline", async () => {
    render(
      <MemoryRouter>
        <Heartbeat />
      </MemoryRouter>,
    );

    expect(await screen.findByText("1/2")).toBeTruthy();
    expect(screen.getByText("97.5%")).toBeTruthy();
    expect(screen.getByText("Unsupported")).toBeTruthy();
    expect(screen.getByText("pre-heartbeat SDK")).toBeTruthy();
    expect(screen.getByLabelText("16 Aug: partial")).toBeTruthy();
    expect(screen.getAllByText("1.5h")).toHaveLength(2);
    expect(screen.getByText("ongoing")).toBeTruthy();
    expect(mocks.getHeartbeatPage).toHaveBeenCalledWith(30);
  });

  it("reloads with the selected history window", async () => {
    render(
      <MemoryRouter>
        <Heartbeat />
      </MemoryRouter>,
    );

    await screen.findByText("1/2");
    fireEvent.click(screen.getByRole("button", { name: "7d" }));

    await waitFor(() =>
      expect(mocks.getHeartbeatPage).toHaveBeenLastCalledWith(7),
    );
  });

  it("surfaces load failures", async () => {
    mocks.getHeartbeatPage.mockRejectedValue(new Error("history unavailable"));

    render(
      <MemoryRouter>
        <Heartbeat />
      </MemoryRouter>,
    );

    expect((await screen.findByRole("alert")).textContent).toContain(
      "history unavailable",
    );
  });

  it("shows the empty-gaps state", async () => {
    mocks.getHeartbeatPage.mockResolvedValue(page({ gaps: [] }));

    render(
      <MemoryRouter>
        <Heartbeat />
      </MemoryRouter>,
    );

    expect(await screen.findByText("No recent gaps")).toBeTruthy();
  });

  it("refreshes when the heartbeat hub publishes an update", async () => {
    render(
      <MemoryRouter>
        <Heartbeat />
      </MemoryRouter>,
    );

    await screen.findByText("1/2");
    expect(mocks.heartbeatUpdate).toBeTypeOf("function");
    act(() => mocks.heartbeatUpdate?.());

    await waitFor(() =>
      expect(mocks.getHeartbeatPage).toHaveBeenCalledTimes(2),
    );
  });
});
