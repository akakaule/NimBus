import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import Sidebar from "./sidebar";
import { simulationStatus } from "components/simulate/simulation-fixtures";

const mocks = vi.hoisted(() => ({
  access: { current: null as Record<string, unknown> | null },
  getAdminSimulation: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") = await vi.importActual("api-client");
  class FakeClient {
    getAdminSimulation = mocks.getAdminSimulation;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});
vi.mock("hooks/use-access", () => ({ useAccess: () => ({ access: mocks.access.current }) }));
vi.mock("hooks/app-status", () => ({ useEnv: () => "dev" }));
vi.mock("components/sidebar-user-footer", () => ({ default: () => null }));

const renderSidebar = () =>
  render(
    <MemoryRouter>
      <Sidebar />
    </MemoryRouter>,
  );

beforeEach(() => {
  mocks.getAdminSimulation.mockReset().mockResolvedValue(simulationStatus({ state: "running" }));
});

afterEach(() => cleanup());

describe("Sidebar Simulate item", () => {
  it("is hidden for a non-owner, who never fetches the simulator status", async () => {
    mocks.access.current = { canManageAccessControl: false, endpointRoles: [] };
    renderSidebar();

    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(screen.queryByRole("link", { name: /Simulate/ })).toBeNull();
    expect(mocks.getAdminSimulation).not.toHaveBeenCalled();
  });

  it("is hidden for an owner while simulate mode is disabled", async () => {
    mocks.access.current = { canManageAccessControl: true, endpointRoles: [] };
    mocks.getAdminSimulation.mockResolvedValue(simulationStatus({ enabled: false }));
    renderSidebar();

    await waitFor(() => expect(mocks.getAdminSimulation).toHaveBeenCalled());
    expect(screen.getByRole("link", { name: /Admin/ })).toBeTruthy();
    expect(screen.queryByRole("link", { name: /Simulate/ })).toBeNull();
  });

  it("is hidden for an owner when the environment blocks simulation", async () => {
    mocks.access.current = { canManageAccessControl: true, endpointRoles: [] };
    mocks.getAdminSimulation.mockResolvedValue(simulationStatus({ allowed: false, blockedReason: "production" }));
    renderSidebar();

    await waitFor(() => expect(mocks.getAdminSimulation).toHaveBeenCalled());
    expect(screen.queryByRole("link", { name: /Simulate/ })).toBeNull();
  });

  it("shows the item with a state badge for an owner when enabled and allowed", async () => {
    mocks.access.current = { canManageAccessControl: true, endpointRoles: [] };
    renderSidebar();

    const link = await screen.findByRole("link", { name: /Simulate/ });
    expect(link.getAttribute("href")).toBe("/Simulate");
    expect(link.textContent).toContain("run");
  });
});
