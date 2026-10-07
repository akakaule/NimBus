import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import OperationsPage from "./operations";

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") = await vi.importActual("api-client");
  class FakeClient {
    getAdminPlatformConfig = vi.fn().mockResolvedValue({ endpoints: [] });
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});
vi.mock("components/admin/endpoint-controls", () => ({
  EndpointControlsCard: () => <div>Endpoint kill switch card</div>,
}));
vi.mock("components/admin/bulk-operations", () => ({
  BulkResubmitCard: () => null,
  DeleteDeadLetteredCard: () => null,
  DeleteEventCard: () => null,
}));
vi.mock("components/admin/advanced-operations", () => ({
  SubscriptionPurgeCard: () => null,
  DeleteByStatusCard: () => null,
  SkipMessagesCard: () => null,
  DeleteMessagesByToCard: () => null,
  CopyEndpointCard: () => null,
  DeleteAllEventsCard: () => null,
}));
vi.mock("components/admin/session-management", () => ({ SessionPurgeCard: () => null }));
vi.mock("components/admin/stale-pending-reconcile", () => ({
  StalePendingReconcileCard: () => null,
}));

const renderPage = () =>
  render(
    <MemoryRouter>
      <OperationsPage />
    </MemoryRouter>,
  );

afterEach(() => cleanup());

describe("Operations page", () => {
  it("leads with the endpoint kill switch, outside the operation groups", () => {
    renderPage();

    expect(screen.getByRole("heading", { name: "Operations" })).toBeTruthy();
    const killSwitch = screen.getByText("Endpoint kill switch card");
    const recovery = screen.getByRole("button", { name: /Recovery/ });
    // Always visible: not behind an accordion trigger, and above the groups.
    expect(screen.queryByRole("button", { name: /Endpoint Kill Switch/ })).toBeNull();
    expect(
      killSwitch.compareDocumentPosition(recovery) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
  });

  it("keeps the blast-radius groups", () => {
    renderPage();

    for (const group of [/Recovery/, /Cleanup/, /Infrastructure/, /Danger Zone/]) {
      expect(screen.getByRole("button", { name: group })).toBeTruthy();
    }
  });
});
