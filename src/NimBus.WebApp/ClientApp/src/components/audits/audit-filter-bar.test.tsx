import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import AuditFilterBar, { EMPTY_AUDIT_FILTER } from "./audit-filter-bar";

vi.mock("api-client", async () => {
  const actual =
    await vi.importActual<typeof import("api-client")>("api-client");
  class FakeClient {
    getEndpointsAll = () => Promise.resolve(["Orders", "Billing"]);
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

beforeEach(() => {
  // Only Date is faked, so React and Testing Library timers keep running.
  vi.useFakeTimers({ toFake: ["Date"] });
  vi.setSystemTime(new Date(2026, 9, 5, 14, 30));
});

afterEach(() => {
  vi.useRealTimers();
  cleanup();
});

describe("AuditFilterBar quick ranges", () => {
  it.each([
    ["Last hour", "2026-10-05T13:30"],
    ["Last 24 hours", "2026-10-04T14:30"],
    ["Last 7 days", "2026-09-28T14:30"],
    ["Last 30 days", "2026-09-05T14:30"],
  ])(
    "%s searches from that far back, open-ended, keeping the other filters",
    (label, createdFrom) => {
      const onSearch = vi.fn();
      render(
        <AuditFilterBar
          value={{
            ...EMPTY_AUDIT_FILTER,
            auditorName: "ops",
            createdTo: "2026-01-01T00:00",
          }}
          onSearch={onSearch}
          onReset={vi.fn()}
          isLoading={false}
        />,
      );

      fireEvent.click(screen.getByRole("button", { name: label }));

      expect(onSearch).toHaveBeenCalledWith({
        ...EMPTY_AUDIT_FILTER,
        auditorName: "ops",
        createdFrom,
        createdTo: "",
      });
    },
  );
});
