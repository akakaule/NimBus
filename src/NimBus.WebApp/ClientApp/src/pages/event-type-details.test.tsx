import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import * as api from "api-client";

const mocks = vi.hoisted(() => ({
  getEventTypeDetails: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual =
    await vi.importActual<typeof import("api-client")>("api-client");

  class FakeClient {
    getEventtypesEventtypeid = mocks.getEventTypeDetails;
  }

  return {
    ...actual,
    Client: FakeClient,
    CookieAuth: () => ({}),
  };
});

vi.mock("components/ui/topology-mini-map", () => ({
  TopologyMiniMap: () => <div data-testid="topology" />,
}));

vi.mock("components/event-types/event-type-properties-table", () => ({
  default: () => <div data-testid="properties" />,
}));

vi.mock("components/event-types/event-type-example-payload", () => ({
  default: () => <div data-testid="example-payload" />,
}));

beforeEach(() => {
  mocks.getEventTypeDetails.mockReset().mockResolvedValue(
    Object.assign(new api.EventTypeDetails(), {
      eventType: Object.assign(new api.EventType(), {
        id: "orders.placed",
        name: "OrderPlaced",
        description: "Published when a customer places a new order.",
        namespace: "NimBus.Events.Orders",
        properties: [],
      }),
      codeRepoLink: "https://example.test/source/OrderPlaced.cs",
      producers: ["StorefrontEndpoint"],
      consumers: ["BillingEndpoint", "WarehouseEndpoint"],
    }),
  );
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("EventTypeDetails", () => {
  it("does not show the source repository link", async () => {
    const { default: EventTypeDetails } = await import("./event-type-details");

    render(
      <MemoryRouter initialEntries={["/EventTypes/Details/orders.placed"]}>
        <Routes>
          <Route
            path="/EventTypes/Details/:id"
            element={<EventTypeDetails />}
          />
        </Routes>
      </MemoryRouter>,
    );

    await waitFor(() => expect(screen.getByText("OrderPlaced")).toBeDefined());

    expect(screen.queryByRole("link", { name: /view source/i })).toBeNull();
  });
});
