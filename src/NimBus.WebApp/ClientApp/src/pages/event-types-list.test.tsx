import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  cleanup,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import * as api from "api-client";

const mocks = vi.hoisted(() => ({
  getEventTypes: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual =
    await vi.importActual<typeof import("api-client")>("api-client");

  class FakeClient {
    getEventTypes = mocks.getEventTypes;
  }

  return {
    ...actual,
    Client: FakeClient,
    CookieAuth: () => ({}),
  };
});

beforeEach(() => {
  mocks.getEventTypes.mockReset().mockResolvedValue([
    Object.assign(new api.EventType(), {
      id: "orders.created",
      name: "OrderCreated",
      namespace: "NimBus.Sales.Events",
      description: "Published when a sales order is created.",
      producerCount: 1,
      consumerCount: 2,
      producers: ["SalesEndpoint"],
      consumers: ["BillingEndpoint", "WarehouseEndpoint"],
    }),
  ]);
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("EventTypesList", () => {
  it("always renders the table with endpoint names and no view switcher", async () => {
    const { default: EventTypesList } = await import("./event-types-list");

    render(
      <MemoryRouter initialEntries={["/EventTypes?viewMode=cards"]}>
        <EventTypesList />
      </MemoryRouter>,
    );

    await waitFor(() => expect(screen.getByText("OrderCreated")).toBeDefined());

    expect(
      screen.getAllByRole("columnheader").map((header) => header.textContent),
    ).toEqual(["Name", "Namespace", "Producers", "Consumers", "Description"]);
    const table = screen.getByRole("table");
    expect(within(table).getByText("SalesEndpoint")).toBeDefined();
    expect(within(table).getByText("BillingEndpoint")).toBeDefined();
    expect(within(table).getByText("WarehouseEndpoint")).toBeDefined();
    expect(screen.queryByRole("button", { name: "Card view" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Table view" })).toBeNull();
  });
});
