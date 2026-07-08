import { describe, it, expect, afterEach, beforeEach, vi } from "vitest";
import { cleanup, render, screen, fireEvent, waitFor } from "@testing-library/react";
import { EndpointControlsCard } from "./endpoint-controls";

const mocks = vi.hoisted(() => ({
  getEndpointSubscriptionstatus: vi.fn(),
  getEndpointSendstatus: vi.fn(),
  postEndpointSubscriptionstatus: vi.fn(),
  postEndpointSendstatus: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") = await vi.importActual("api-client");
  class FakeClient {
    getEndpointSubscriptionstatus = mocks.getEndpointSubscriptionstatus;
    getEndpointSendstatus = mocks.getEndpointSendstatus;
    postEndpointSubscriptionstatus = mocks.postEndpointSubscriptionstatus;
    postEndpointSendstatus = mocks.postEndpointSendstatus;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

const endpoints = [{ value: "ep-1", label: "ep-1" }];

beforeEach(() => {
  // Receive already disabled, Send active — one "Enable" (receive) + one "Disable" (send).
  mocks.getEndpointSubscriptionstatus.mockReset().mockResolvedValue("disabled");
  mocks.getEndpointSendstatus.mockReset().mockResolvedValue("active");
  mocks.postEndpointSubscriptionstatus.mockReset().mockResolvedValue(undefined);
  mocks.postEndpointSendstatus.mockReset().mockResolvedValue(undefined);
});

afterEach(() => cleanup());

describe("EndpointControlsCard", () => {
  it("disables send only after confirming, calling postEndpointSendstatus('disable')", async () => {
    render(<EndpointControlsCard endpoints={endpoints} />);

    // Row "Disable" (send) appears once status has loaded.
    const disableBtn = await screen.findByRole("button", { name: "Disable" });
    fireEvent.click(disableBtn);

    // Confirm modal — nothing sent yet.
    expect(mocks.postEndpointSendstatus).not.toHaveBeenCalled();
    const confirmBtn = await screen.findByRole("button", { name: "Disable send" });
    fireEvent.click(confirmBtn);

    await waitFor(() =>
      expect(mocks.postEndpointSendstatus).toHaveBeenCalledWith("ep-1", "disable"),
    );
  });

  it("enables receive immediately (no confirm), calling postEndpointSubscriptionstatus('enable')", async () => {
    render(<EndpointControlsCard endpoints={endpoints} />);

    const enableBtn = await screen.findByRole("button", { name: "Enable" });
    fireEvent.click(enableBtn);

    await waitFor(() =>
      expect(mocks.postEndpointSubscriptionstatus).toHaveBeenCalledWith("ep-1", "enable"),
    );
  });
});
