import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import SimulationSettings from "./simulation-settings";
import { simulationStatus } from "components/simulate/simulation-fixtures";

const mocks = vi.hoisted(() => ({
  getAdminSimulation: vi.fn(),
  putAdminSimulationSettings: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") = await vi.importActual("api-client");
  class FakeClient {
    getAdminSimulation = mocks.getAdminSimulation;
    putAdminSimulationSettings = mocks.putAdminSimulationSettings;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

const renderTab = () =>
  render(
    <MemoryRouter>
      <SimulationSettings />
    </MemoryRouter>,
  );

beforeEach(() => {
  mocks.getAdminSimulation.mockReset().mockResolvedValue(simulationStatus({ settings: { enabled: true, autoStopMinutes: 60, rateCeilingPerMinute: 600, ownedEndpoints: [] } }));
  mocks.putAdminSimulationSettings.mockReset().mockImplementation((body: { ownedEndpoints: string[] }) =>
    Promise.resolve(simulationStatus({ settings: body })),
  );
});

afterEach(() => cleanup());

describe("Admin → Simulation", () => {
  it("lists consuming endpoints with ownership unchecked by default", async () => {
    renderTab();

    const billing = (await screen.findByLabelText("Simulator owns BillingEndpoint")) as HTMLInputElement;
    const warehouse = screen.getByLabelText("Simulator owns WarehouseEndpoint") as HTMLInputElement;
    expect(billing.checked).toBe(false);
    expect(warehouse.checked).toBe(false);
    expect(screen.queryByLabelText("Simulator owns StorefrontEndpoint")).toBeNull();
    expect(screen.getAllByText("permanently blocked")).toHaveLength(4);
  });

  it("asks for confirmation before taking ownership and saves ownedEndpoints", async () => {
    renderTab();

    fireEvent.click(await screen.findByLabelText("Simulator owns WarehouseEndpoint"));
    expect(screen.getByRole("dialog")).toBeTruthy();
    expect((screen.getByLabelText("Simulator owns WarehouseEndpoint") as HTMLInputElement).checked).toBe(false);

    fireEvent.click(screen.getByRole("button", { name: "Take ownership" }));
    expect((screen.getByLabelText("Simulator owns WarehouseEndpoint") as HTMLInputElement).checked).toBe(true);

    fireEvent.click(screen.getByRole("button", { name: "Save simulation settings" }));
    await waitFor(() => expect(mocks.putAdminSimulationSettings).toHaveBeenCalledTimes(1));
    const body = mocks.putAdminSimulationSettings.mock.calls[0][0];
    expect(body.ownedEndpoints).toEqual(["WarehouseEndpoint"]);
    expect(body.enabled).toBe(true);
    expect(body.autoStopMinutes).toBe(60);
    expect(body.rateCeilingPerMinute).toBe(600);
  });

  it("cancelling the confirmation leaves the endpoint External", async () => {
    renderTab();

    fireEvent.click(await screen.findByLabelText("Simulator owns BillingEndpoint"));
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    expect((screen.getByLabelText("Simulator owns BillingEndpoint") as HTMLInputElement).checked).toBe(false);
  });

  it.each([
    ["environmentMissing", "", /missing or blank/],
    ["notAllowed", "test", /not in NimBus:Simulation:AllowedEnvironments/],
    ["production", "prod", /can never run in production/],
  ])("disables every control when blocked (%s)", async (reason, environment, text) => {
    mocks.getAdminSimulation.mockResolvedValue(simulationStatus({ allowed: false, blockedReason: reason, environment }));
    renderTab();

    expect((await screen.findByRole("alert")).textContent).toMatch(text);
    expect((screen.getByLabelText("Simulator owns BillingEndpoint") as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByRole("switch", { name: "Enable simulate mode" }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole("button", { name: "Save simulation settings" }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.queryByRole("link", { name: /Open Simulate/ })).toBeNull();
  });

  it("shows the server's violations when a save is rejected", async () => {
    mocks.putAdminSimulationSettings.mockRejectedValue({ errors: ["Endpoint ownership cannot change while the simulation is running."] });
    renderTab();

    fireEvent.click(await screen.findByRole("button", { name: "Save simulation settings" }));

    expect((await screen.findByRole("alert")).textContent).toMatch(/cannot change while the simulation is running/);
  });
});
