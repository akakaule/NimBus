import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import * as api from "api-client";
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

const renderSidebar = (path = "/Endpoints") =>
  render(
    <MemoryRouter initialEntries={[path]}>
      <Sidebar />
    </MemoryRouter>,
  );

const MANAGE_PAGES = {
  Operations: "/Operations",
  Topology: "/Topology",
  Settings: "/Settings",
};

beforeEach(() => {
  mocks.getAdminSimulation.mockReset().mockResolvedValue(simulationStatus({ enabled: false }));
});

afterEach(() => cleanup());

// Spec 038 §5.1: the Admin page is split into three site-Owner pages.
describe("Sidebar Manage pages", () => {
  it("shows Operations, Topology and Settings to a site Owner, and no Admin item", async () => {
    mocks.access.current = { canManageAccessControl: true, endpointRoles: [] };
    renderSidebar();

    await waitFor(() => expect(mocks.getAdminSimulation).toHaveBeenCalled());
    for (const [name, href] of Object.entries(MANAGE_PAGES)) {
      expect(screen.getByRole("link", { name }).getAttribute("href")).toBe(href);
    }
    expect(screen.queryByRole("link", { name: /Admin/ })).toBeNull();
  });

  it("hides them from an endpoint Owner, who keeps Access Control", async () => {
    mocks.access.current = {
      canManageAccessControl: false,
      endpointRoles: [{ endpointId: "crm", role: api.EndpointRoleInfoRole.Owner }],
    };
    renderSidebar();

    expect(await screen.findByRole("link", { name: "Access Control" })).toBeTruthy();
    for (const name of Object.keys(MANAGE_PAGES)) {
      expect(screen.queryByRole("link", { name })).toBeNull();
    }
  });

  it.each([
    ["/Topology/drift", "Topology"],
    ["/Settings/mcp", "Settings"],
  ])("keeps the page highlighted on %s", async (path, name) => {
    mocks.access.current = { canManageAccessControl: true, endpointRoles: [] };
    renderSidebar(path);

    const link = await screen.findByRole("link", { name });
    expect(link.getAttribute("aria-current")).toBe("page");
  });
});
