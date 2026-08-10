import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, renderHook } from "@testing-library/react";
import { reportedCellState } from "functions/reported.functions";
import {
  EMPTY_STATUS,
  FULL_STATUS,
  settleAppStatus,
  stubStatusFetch,
} from "../test-utils/stub-app-status";

// AC4: with TicketLinkTemplate configured, a reported event must still render a
// working ticket deep link after sign-in. The pure builder is covered in
// functions/reported.functions.test.ts; what GH#93 touches is the composition —
// the API response feeding useTicketLinkTemplate feeding the builder.
describe("useTicketLinkTemplate -> reported cell deep link", () => {
  beforeEach(() => {
    vi.resetModules();
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("builds a working ticket link for an authenticated caller", async () => {
    const fetchMock = stubStatusFetch(() => FULL_STATUS);
    const mod = await import("hooks/app-status");

    const { result } = renderHook(() => mod.useTicketLinkTemplate());
    await settleAppStatus(mod);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(result.current).toBe(FULL_STATUS.ticketLinkTemplate);

    const cell = reportedCellState({
      isReported: true,
      ticketId: "INC-42",
      ticketLinkTemplate: result.current,
    });

    expect(cell.kind).toBe("ticket");
    expect(cell.kind === "ticket" && cell.href).toBe(
      "https://tickets.example.com/INC-42",
    );
  });

  it("falls back to the plain badge for the anonymous body", async () => {
    const fetchMock = stubStatusFetch(() => EMPTY_STATUS);
    const mod = await import("hooks/app-status");

    const { result } = renderHook(() => mod.useTicketLinkTemplate());
    await settleAppStatus(mod);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(result.current).toBeUndefined();

    const cell = reportedCellState({
      isReported: true,
      ticketId: "INC-42",
      ticketLinkTemplate: result.current,
    });

    expect(cell.kind).toBe("ticket");
    expect(cell.kind === "ticket" && cell.href).toBeUndefined();
  });
});
