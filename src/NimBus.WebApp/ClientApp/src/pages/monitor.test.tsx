import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import Monitor, {
  BACKLOG_ELEVATED,
  BACKLOG_HIGH,
  backlogLevel,
  backlogTotal,
  endpointState,
  failedPerMinuteSeries,
  firstOpenFailureAt,
  formatElapsed,
} from "./monitor";
import type {
  FleetSample,
  MonitorEndpoint,
  UseMonitorDataResult,
} from "hooks/use-monitor-data";

const mocks = vi.hoisted(() => ({
  endpoints: [] as MonitorEndpoint[],
  telemetry: [] as FleetSample[],
  access: null as unknown,
}));

vi.mock("hooks/use-monitor-data", () => ({
  useMonitorData: (): UseMonitorDataResult => ({
    endpoints: mocks.endpoints,
    lastRefreshAt: Date.now(),
    loading: false,
    error: undefined,
    isStale: false,
    ticker: [],
    telemetry: mocks.telemetry,
    ack: vi.fn(),
    unack: vi.fn(),
    refresh: vi.fn(),
  }),
}));

vi.mock("hooks/use-access", async (importOriginal) => ({
  ...(await importOriginal<typeof import("hooks/use-access")>()),
  useAccess: () => ({ access: mocks.access }),
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
  deadletter?: number;
  firstFailureAt?: number;
  acked?: boolean;
  acknowledgedBy?: string;
  /** Backlog at the oldest sample — lower than current means "growing". */
  pendingWas?: number;
}

function endpoint({
  id = "ep",
  pending = 0,
  deferred = 0,
  failed = 0,
  deadletter = 0,
  firstFailureAt,
  acked = false,
  acknowledgedBy,
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
      deadletterCount: deadletter,
    } as MonitorEndpoint["status"],
    samples,
    firstFailureAt,
    isFreshFailure: false,
    ack: acked
      ? {
          reason: "",
          ackedAt: now,
          failedAtAck: failed,
          expiresAt: now + 4 * 60 * 60 * 1000,
          acknowledgedBy,
        }
      : undefined,
  };
}

afterEach(() => {
  mocks.endpoints = [];
  mocks.telemetry = [];
  mocks.access = null;
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

    expect(screen.getByText("high")).toBeDefined();
    expect(screen.queryAllByText("elevated")).toHaveLength(0);
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

describe("endpointState", () => {
  it("classifies failing, acknowledged, backlog and healthy endpoints", () => {
    expect(endpointState(endpoint({ failed: 3 }))).toBe("nogo");
    expect(endpointState(endpoint({ failed: 3, acked: true }))).toBe("acked");
    expect(endpointState(endpoint({ pending: 4 }))).toBe("hold");
    expect(endpointState(endpoint({ deferred: 2 }))).toBe("hold");
    expect(endpointState(endpoint({}))).toBe("go");
  });
});

describe("firstOpenFailureAt", () => {
  it("returns the earliest unacknowledged failure and ignores acked ones", () => {
    expect(
      firstOpenFailureAt([
        endpoint({ id: "a", failed: 1, firstFailureAt: 3_000 }),
        endpoint({ id: "b", failed: 1, firstFailureAt: 1_000, acked: true }),
        endpoint({ id: "c", failed: 1, firstFailureAt: 2_000 }),
        endpoint({ id: "d", pending: 9 }),
      ]),
    ).toBe(2_000);
  });

  it("is undefined when nothing is failing unacknowledged", () => {
    expect(
      firstOpenFailureAt([
        endpoint({ failed: 1, firstFailureAt: 1_000, acked: true }),
      ]),
    ).toBeUndefined();
  });
});

describe("formatElapsed", () => {
  it("adds a day count once a failure is older than 24 hours", () => {
    expect(formatElapsed((6 * 3600 + 24 * 60 + 23) * 1000)).toBe("06:24:23");
    expect(formatElapsed(((64 * 24 + 6) * 3600 + 24 * 60 + 23) * 1000)).toBe(
      "64d 06:24:23",
    );
  });
});

describe("failedPerMinuteSeries", () => {
  const sample = (t: number, failed: number): FleetSample => ({
    t,
    failed,
    backlog: 0,
  });

  it("turns cumulative failed totals into failures per minute", () => {
    const telemetry = [0, 1, 2, 3].map((i) => sample(i * 5_000, i * 10));
    expect(failedPerMinuteSeries(telemetry)).toEqual([0, 120, 120, 120]);
  });

  it("never goes negative when failures are resubmitted or skipped", () => {
    const telemetry = [sample(0, 50), sample(5_000, 20)];
    expect(failedPerMinuteSeries(telemetry)).toEqual([0, 0]);
  });
});

describe("Monitor wall", () => {
  it("summarises the fleet in the GO ring", () => {
    mocks.endpoints = [
      endpoint({ id: "AEndpoint" }),
      endpoint({ id: "BEndpoint", failed: 2, firstFailureAt: Date.now() }),
      endpoint({ id: "CEndpoint" }),
    ];

    render(<Monitor />);

    expect(screen.getByRole("img", { name: "2 of 3 endpoints GO" })).toBeDefined();
  });

  it("shows dead-lettered messages as part of the failed count", () => {
    mocks.endpoints = [
      endpoint({
        id: "TracetoolEndpoint",
        failed: 208,
        deadletter: 16,
        firstFailureAt: Date.now(),
      }),
    ];

    render(<Monitor />);

    expect(screen.getByText("incl. DLQ")).toBeDefined();
    expect(screen.getByText("16")).toBeDefined();
  });

  it("counts failing time from the oldest open failure", () => {
    mocks.endpoints = [
      endpoint({
        id: "BillingEndpoint",
        failed: 4,
        firstFailureAt: Date.now() - (3 * 60 + 20) * 60 * 1000,
      }),
    ];

    render(<Monitor />);

    expect(screen.getByText("Failing for 3h 20m")).toBeDefined();
    expect(screen.getByText("T+ since first failure")).toBeDefined();
  });

  it("names who acknowledged a failure", () => {
    mocks.endpoints = [
      endpoint({
        id: "BillingEndpoint",
        failed: 4,
        firstFailureAt: Date.now(),
        acked: true,
        acknowledgedBy: "alice@example.com",
      }),
    ];

    render(<Monitor />);

    expect(screen.getByText("Acked 0s ago by alice")).toBeDefined();
  });

  it("shows readers the ACK state without letting them change it", () => {
    mocks.access = {
      siteRole: "reader",
      endpointRoles: [{ endpointId: "billingendpoint", role: "contributor" }],
    };
    mocks.endpoints = [
      endpoint({ id: "BillingEndpoint", failed: 4, firstFailureAt: Date.now() }),
      endpoint({
        id: "OrdersEndpoint",
        failed: 2,
        firstFailureAt: Date.now(),
        acked: true,
      }),
    ];

    render(<Monitor />);

    const ack = screen.getByRole("button", { name: "ack" }) as HTMLButtonElement;
    const acked = screen.getByRole("button", { name: "✓ acked" }) as HTMLButtonElement;
    expect(ack.disabled).toBe(false);
    expect(acked.disabled).toBe(true);
  });

  it("says so when nothing is failing", () => {
    mocks.endpoints = [endpoint({ id: "AEndpoint" })];

    render(<Monitor />);

    expect(screen.getByText("No failing endpoints")).toBeDefined();
    expect(screen.getByText("No open failures")).toBeDefined();
  });
});

describe("Monitor backlog drain ETA", () => {
  it("does not call a flat queue growing", () => {
    mocks.endpoints = [
      endpoint({ id: "MitHrEndpoint", pending: 880, pendingWas: 879.8 }),
    ];

    render(<Monitor />);

    expect(screen.getByText("stable")).toBeDefined();
    expect(screen.queryByText("growing")).toBeNull();
  });
});
