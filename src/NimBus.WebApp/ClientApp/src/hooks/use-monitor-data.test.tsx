import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  useMonitorData,
  STALE_AFTER_MS,
  TELEMETRY_LEN,
} from "./use-monitor-data";

// Call counters shared with the api-client mock below.
const statusCountCalls = vi.fn();
let failRequests = false;
let failedCount = 0;

vi.mock("api-client", () => {
  class Client {
    getEndpointsAll() {
      return Promise.resolve(["ep-1"]);
    }
    postApiEndpointStatusCount(ids: string[]) {
      statusCountCalls(ids);
      if (failRequests) {
        return Promise.reject(new Error("api down"));
      }
      return Promise.resolve(
        ids.map((id) => ({
          endpointId: id,
          failedCount,
          pendingCount: 7,
          deferredCount: 3,
        })),
      );
    }
  }
  return { Client, CookieAuth: () => ({}) };
});

const REFRESH_MS = 5_000;

function setDocumentHidden(hidden: boolean) {
  Object.defineProperty(document, "hidden", {
    configurable: true,
    get: () => hidden,
  });
}

async function flushAsync() {
  // Drain the microtask queue so in-flight fetch promises settle under fake timers.
  for (let i = 0; i < 10; i++) {
    await act(async () => {
      await Promise.resolve();
    });
  }
}

describe("useMonitorData visibility-aware polling", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    statusCountCalls.mockClear();
    failRequests = false;
    failedCount = 0;
    setDocumentHidden(false);
    // Node 25's experimental localStorage can leave the global undefined in
    // jsdom runs; the hook guards its own access, so just clear when present.
    window.localStorage?.clear?.();
  });

  afterEach(() => {
    vi.useRealTimers();
    setDocumentHidden(false);
  });

  it("skips polling ticks while the tab is hidden", async () => {
    const { unmount } = renderHook(() => useMonitorData());
    await flushAsync();
    expect(statusCountCalls).toHaveBeenCalledTimes(1);

    setDocumentHidden(true);
    await act(async () => {
      vi.advanceTimersByTime(REFRESH_MS * 3);
    });
    await flushAsync();

    expect(statusCountCalls).toHaveBeenCalledTimes(1);
    unmount();
  });

  it("refreshes immediately when the tab becomes visible again", async () => {
    const { unmount } = renderHook(() => useMonitorData());
    await flushAsync();
    expect(statusCountCalls).toHaveBeenCalledTimes(1);

    setDocumentHidden(true);
    await act(async () => {
      vi.advanceTimersByTime(REFRESH_MS * 2);
    });
    await flushAsync();
    expect(statusCountCalls).toHaveBeenCalledTimes(1);

    setDocumentHidden(false);
    await act(async () => {
      document.dispatchEvent(new Event("visibilitychange"));
    });
    await flushAsync();

    expect(statusCountCalls).toHaveBeenCalledTimes(2);
    unmount();
  });

  it("does not flag stale right after resuming a long-hidden tab", async () => {
    const { result, unmount } = renderHook(() => useMonitorData());
    await flushAsync();
    expect(result.current.isStale).toBe(false);

    // Hide the tab and let far more than STALE_AFTER_MS pass without polls.
    setDocumentHidden(true);
    failRequests = true; // even the resume fetch fails — banner must still wait
    await act(async () => {
      vi.advanceTimersByTime(STALE_AFTER_MS * 4);
    });
    await flushAsync();

    setDocumentHidden(false);
    await act(async () => {
      document.dispatchEvent(new Event("visibilitychange"));
    });
    await flushAsync();
    // Advance only the 1 Hz staleness tick, well inside the grace window.
    await act(async () => {
      vi.advanceTimersByTime(1_000);
    });

    expect(result.current.isStale).toBe(false);
    unmount();
  });

  it("still flags stale when visible polling keeps failing", async () => {
    const { result, unmount } = renderHook(() => useMonitorData());
    await flushAsync();
    expect(result.current.isStale).toBe(false);

    failRequests = true;
    await act(async () => {
      vi.advanceTimersByTime(STALE_AFTER_MS + REFRESH_MS + 1_000);
    });
    await flushAsync();
    // One more 1 Hz tick so `now` re-derives after the last failed poll.
    await act(async () => {
      vi.advanceTimersByTime(1_000);
    });

    expect(result.current.isStale).toBe(true);
    unmount();
  });
});

describe("useMonitorData fleet telemetry", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    failRequests = false;
    failedCount = 0;
    setDocumentHidden(false);
    window.localStorage?.clear?.();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  async function poll() {
    await act(async () => {
      vi.advanceTimersByTime(REFRESH_MS);
    });
    await flushAsync();
  }

  it("records one fleet sample per poll with failed and backlog totals", async () => {
    const { result, unmount } = renderHook(() => useMonitorData());
    await flushAsync();
    failedCount = 4;
    await poll();

    const telemetry = result.current.telemetry;
    expect(telemetry).toHaveLength(2);
    expect(telemetry[0]).toMatchObject({ failed: 0, backlog: 10 });
    expect(telemetry[1]).toMatchObject({ failed: 4, backlog: 10 });
    expect(telemetry[1].t - telemetry[0].t).toBe(REFRESH_MS);
    unmount();
  });

  it("keeps at most TELEMETRY_LEN samples (10 minutes at 5 s)", async () => {
    expect(TELEMETRY_LEN).toBe(120);
    const { result, unmount } = renderHook(() => useMonitorData());
    await flushAsync();
    for (let i = 0; i < TELEMETRY_LEN + 3; i++) {
      await poll();
    }

    expect(result.current.telemetry).toHaveLength(TELEMETRY_LEN);
    unmount();
  });
});

describe("useMonitorData fresh failures", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    failRequests = false;
    failedCount = 0;
    setDocumentHidden(false);
    window.localStorage?.clear?.();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("does not flag failures that already existed when the page loaded", async () => {
    failedCount = 5;
    const { result, unmount } = renderHook(() => useMonitorData());
    await flushAsync();

    expect(result.current.endpoints[0].isFreshFailure).toBe(false);
    unmount();
  });

  it("flags a failure the page saw start", async () => {
    const { result, unmount } = renderHook(() => useMonitorData());
    await flushAsync();
    failedCount = 5;
    await act(async () => {
      vi.advanceTimersByTime(REFRESH_MS);
    });
    await flushAsync();

    expect(result.current.endpoints[0].isFreshFailure).toBe(true);
    unmount();
  });
});
