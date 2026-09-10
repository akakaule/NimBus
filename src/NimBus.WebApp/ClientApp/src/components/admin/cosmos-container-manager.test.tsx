import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

const { listMock, deleteMock } = vi.hoisted(() => ({
  listMock: vi.fn(),
  deleteMock: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual =
    await vi.importActual<typeof import("api-client")>("api-client");
  class Client {
    getAdminCosmosContainers = listMock;
    postAdminCosmosContainerDelete = deleteMock;
  }
  return { ...actual, Client, CookieAuth: () => ({}) };
});

afterEach(() => {
  cleanup();
  vi.resetAllMocks();
});

describe("CosmosContainerManager", () => {
  it("lists orphaned containers and deletes after exact typed confirmation", async () => {
    listMock
      .mockResolvedValueOnce([{ name: "OldEndpoint" }])
      .mockResolvedValueOnce([]);
    deleteMock.mockResolvedValue({ name: "OldEndpoint", deleted: true });
    const { default: CosmosContainerManager } =
      await import("./cosmos-container-manager");
    render(<CosmosContainerManager />);

    expect(await screen.findByText("OldEndpoint")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Delete" }));
    const confirmation = screen.getByPlaceholderText("OldEndpoint");
    await userEvent.type(confirmation, "oldendpoint");
    expect(
      (
        screen.getByRole("button", {
          name: "Permanently delete container",
        }) as HTMLButtonElement
      ).disabled,
    ).toBe(true);
    await userEvent.clear(confirmation);
    await userEvent.type(confirmation, "OldEndpoint");
    await userEvent.click(
      screen.getByRole("button", { name: "Permanently delete container" }),
    );

    await waitFor(() => expect(deleteMock).toHaveBeenCalledTimes(1));
    expect(deleteMock.mock.calls[0][0].confirmation).toBe("OldEndpoint");
    expect(deleteMock.mock.calls[0][1]).toBe("OldEndpoint");
    expect(
      await screen.findByText("No containers outside the current platform."),
    ).toBeTruthy();
  }, 10_000);

  it("explains when Cosmos DB is not configured", async () => {
    listMock.mockRejectedValue({ status: 404 });
    const { default: CosmosContainerManager } =
      await import("./cosmos-container-manager");
    render(<CosmosContainerManager />);
    expect(
      await screen.findByText(/Cosmos DB storage is not configured/),
    ).toBeTruthy();
  });
});
