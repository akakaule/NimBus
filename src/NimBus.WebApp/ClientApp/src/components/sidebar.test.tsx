import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import {
  EMPTY_STATUS,
  FULL_STATUS,
  settleAppStatus,
  stubStatusFetch,
} from "../test-utils/stub-app-status";

// AC4: after signing in, the sidebar must still show the environment badge.
// useAccess swallows its own failure and SidebarUserFooter's /api/auth/me is
// answered `{}` by the routed stub, so no extra mock is needed.
describe("Sidebar environment badge", () => {
  beforeEach(() => {
    vi.resetModules();
    Object.defineProperty(window, "matchMedia", {
      configurable: true,
      writable: true,
      value: vi.fn().mockImplementation((query: string) => ({
        matches: false,
        media: query,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      })),
    });
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("shows the environment badge for an authenticated caller", async () => {
    stubStatusFetch(() => FULL_STATUS);
    const hooks = await import("hooks/app-status");
    const { default: Sidebar } = await import("components/sidebar");

    render(
      <MemoryRouter>
        <Sidebar />
      </MemoryRouter>,
    );
    await settleAppStatus(hooks);

    expect(screen.getAllByText(/ProdCanary/).length).toBeGreaterThan(0);
    expect(
      screen.getByRole("link", { name: "Heartbeat" }).getAttribute("href"),
    ).toBe("/Heartbeat");
  });

  it("renders without the environment badge for the anonymous body", async () => {
    stubStatusFetch(() => EMPTY_STATUS);
    const hooks = await import("hooks/app-status");
    const { default: Sidebar } = await import("components/sidebar");

    render(
      <MemoryRouter>
        <Sidebar />
      </MemoryRouter>,
    );
    await settleAppStatus(hooks);

    expect(screen.queryAllByText(/ProdCanary/)).toHaveLength(0);
    // The static half still renders — proof the component did not blow up.
    expect(screen.getByText("NimBus")).toBeTruthy();
  });
});
