import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import Monitor, {
  BACKLOG_ELEVATED,
  BACKLOG_HIGH,
  backlogLevel,
  backlogTotal,
} from "./monitor";
import type {
  MonitorEndpoint,
  UseMonitorDataResult,
} from "hooks/use-monitor-data";

const mocks = vi.hoisted(() => ({
  endpoints: [] as MonitorEndpoint[],
}));

vi.mock("hooks/use-monitor-data", () => ({
  useMonitorData: (): UseMonitorDataResult => ({
    endpoints: mocks.endpoints,
    lastRefreshAt: Date.now(),
    loading: false,
    error: undefined,
    isStale: false,
    ticker: [],
    ack: vi.fn(),
    unack: vi.fn(),
    refresh: vi.fn(),
  }),
}));

vi.mock("hooks/app-status", () => ({
  useEnv: () => "dev",
  getApplicationStatus: () => Promise.resolve({ platformName: "Test" }),
}));

interface EndpointOptions {
  id?: string;
  pending?: number;
  deferred?: number;
  failed?: number;
  /** Backlog at the oldest sample — lower than current means "growing". */
  pendingWas?: number;
}

function endpoint({
  id = "ep",
  pending = 0,
  deferred = 0,
  failed = 0,
  pendingWas,
}: EndpointOptions): MonitorEndpoint {
  const now = Date.now();
  const samples =
    pendingWas === undefined
      ? []
      : [
          { t: now - 60_000, failed, pending: pendingWas, deferred },
          { t: now, failed, pending, deferred },
        ];
  return {
    id,
    status: {
      endpointId: id,
      failedCount: failed,
      pendingCount: pending,
      deferredCount: deferred,
    } as MonitorEndpoint["status"],
    samples,
    isFreshFailure: false,
  };
}

afterEach(() => {
  mocks.endpoints = [];
  cleanup();
});

describe("backlogTotal", () => {
  it("counts pending and deferred together", () => {
    expect(backlogTotal(endpoint({ pending: 10, deferred: 5 }))).toBe(15);
  });
});

describe("backlogLevel", () => {
  it("stays normal below the elevated threshold", () => {
    expect(backlogLevel(endpoint({ pending: BACKLOG_ELEVATED - 1 }))).toBe(
      "normal",
    );
  });

  it("is elevated at the threshold when the queue is not growing", () => {
    expect(
      backlogLevel(
        endpoint({ pending: BACKLOG_ELEVATED, pendingWas: BACKLOG_ELEVATED }),
      ),
    ).toBe("elevated");
  });

  it("is high at the high threshold", () => {
    expect(backlogLevel(endpoint({ pending: BACKLOG_HIGH }))).toBe("high");
  });

  it("escalates an elevated backlog that is still growing", () => {
    expect(
      backlogLevel(
        endpoint({ pending: BACKLOG_ELEVATED + 10, pendingWas: 1 }),
      ),
    ).toBe("high");
  });

  it("does not escalate a small backlog just because it is growing", () => {
    expect(backlogLevel(endpoint({ pending: 7, pendingWas: 1 }))).toBe(
      "normal",
    );
  });
});

describe("Monitor backlog highlight", () => {
  it("tags a deep backlog and leaves a small one alone", () => {
    mocks.endpoints = [
      endpoint({ id: "DataPlatformEndpoint", pending: BACKLOG_HIGH + 22 }),
      endpoint({ id: "AgentZoneEndpoint", pending: 7 }),
    ];

    render(<Monitor />);

    expect(screen.getByText("high backlog")).toBeDefined();
    expect(screen.queryAllByText("backlog")).toHaveLength(0);
    expect(
      screen.getByText(`1 endpoint at or above ${BACKLOG_ELEVATED} pending`),
    ).toBeDefined();
  });

  it("reports an all-quiet backlog band when every queue is shallow", () => {
    mocks.endpoints = [endpoint({ id: "AgentZoneEndpoint", pending: 7 })];

    render(<Monitor />);

    expect(
      screen.getByText(`all below ${BACKLOG_ELEVATED} pending`),
    ).toBeDefined();
  });
});
