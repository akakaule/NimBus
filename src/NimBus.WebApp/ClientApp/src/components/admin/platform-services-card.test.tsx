import { describe, it, expect, afterEach, beforeEach, vi } from "vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import PlatformServicesCard from "./platform-services-card";

const mocks = vi.hoisted(() => ({
  getAdminHealthServices: vi.fn(),
  subscribeServiceHealthUpdates: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") = await vi.importActual("api-client");
  class FakeClient {
    getAdminHealthServices = mocks.getAdminHealthServices;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

// No real hub connection in tests — the subscription is a no-op that never
// reports "connected", which also exercises the degraded/polling branch.
vi.mock("lib/grid-events-connection", () => ({
  subscribeServiceHealthUpdates: mocks.subscribeServiceHealthUpdates,
}));

function serviceRow(overrides: Record<string, unknown> = {}) {
  return {
    serviceId: "Resolver",
    status: "On",
    version: "1.4.2",
    roundTripMs: 42,
    probeInFlight: false,
    ...overrides,
  };
}

beforeEach(() => {
  mocks.getAdminHealthServices.mockReset().mockResolvedValue([serviceRow()]);
  mocks.subscribeServiceHealthUpdates
    .mockReset()
    .mockReturnValue({ dispose: () => {} });
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("PlatformServicesCard", () => {
  it("renders the Resolver row with its status badge and round-trip", async () => {
    render(<PlatformServicesCard />);

    const cell = await screen.findByText("Resolver");
    const row = cell.closest("tr") as HTMLTableRowElement;
    expect(within(row).getByText("On")).toBeTruthy();
    expect(within(row).getByText("42 ms")).toBeTruthy();
    expect(within(row).getByText("1.4.2")).toBeTruthy();
  });

  it("shows Unknown and placeholders before a probe has settled", async () => {
    mocks.getAdminHealthServices.mockResolvedValue([
      serviceRow({ status: "Unknown", version: undefined, roundTripMs: undefined }),
    ]);
    render(<PlatformServicesCard />);

    const cell = await screen.findByText("Resolver");
    const row = cell.closest("tr") as HTMLTableRowElement;
    expect(within(row).getByText("Unknown")).toBeTruthy();
    expect(within(row).getByText("unknown")).toBeTruthy();
    expect(within(row).getAllByText("—").length).toBeGreaterThan(0);
    expect(within(row).getByText("never")).toBeTruthy();
  });

  it("flags a probe still in flight without changing the settled status", async () => {
    mocks.getAdminHealthServices.mockResolvedValue([
      serviceRow({ status: "On", probeInFlight: true }),
    ]);
    render(<PlatformServicesCard />);

    const cell = await screen.findByText("Resolver");
    const row = cell.closest("tr") as HTMLTableRowElement;
    expect(within(row).getByText("On")).toBeTruthy();
    expect(within(row).getByText("probing…")).toBeTruthy();
  });

  it("surfaces a failed load instead of rendering an empty 'no services' table", async () => {
    mocks.getAdminHealthServices.mockRejectedValue(new Error("boom"));
    render(<PlatformServicesCard />);

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("boom");
    await waitFor(() => expect(screen.queryByText("No services.")).toBeNull());
  });
});
