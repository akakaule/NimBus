import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
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
      .mockResolvedValueOnce([{ name: "OldEndpoint", isInPlatform: false }])
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
      await screen.findByText("No containers match this filter."),
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

const mixedContainers = [
  { name: "CurrentEndpoint", isInPlatform: true },
  { name: "messages", isInPlatform: true },
  { name: "OldEndpoint", isInPlatform: false },
  { name: "OtherEndpoint", isInPlatform: false },
];

async function renderManager() {
  const { default: CosmosContainerManager } =
    await import("./cosmos-container-manager");
  render(<CosmosContainerManager />);
  await screen.findByText("OldEndpoint");
}

it("filters by platform membership and only selects visible deletable containers", async () => {
  listMock.mockResolvedValue(mixedContainers);
  await renderManager();
  expect(
    (
      screen.getByRole("checkbox", {
        name: "Select CurrentEndpoint",
      }) as HTMLInputElement
    ).disabled,
  ).toBe(true);
  await userEvent.click(
    screen.getByRole("checkbox", {
      name: "Select all visible non-platform containers",
    }),
  );
  expect(
    screen.getByRole("button", { name: "Delete selected (2)" }),
  ).toBeTruthy();
  await userEvent.selectOptions(
    screen.getByLabelText("Platform membership"),
    "platform",
  );
  expect(screen.queryByText("OldEndpoint")).toBeNull();
  expect(screen.getByText("CurrentEndpoint")).toBeTruthy();
  expect(
    (
      screen.getByRole("button", {
        name: "Delete selected (0)",
      }) as HTMLButtonElement
    ).disabled,
  ).toBe(true);
  await userEvent.selectOptions(
    screen.getByLabelText("Platform membership"),
    "outside",
  );
  expect(screen.queryByText("CurrentEndpoint")).toBeNull();
  expect(screen.getByText("OldEndpoint")).toBeTruthy();
  expect(deleteMock).not.toHaveBeenCalled();
});

it("warns and requires confirmation for bulk deletion, and supports cancellation", async () => {
  listMock.mockResolvedValue(mixedContainers);
  deleteMock.mockResolvedValue({ deleted: true });
  await renderManager();
  await userEvent.click(
    screen.getByRole("checkbox", {
      name: "Select all visible non-platform containers",
    }),
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Delete selected (2)" }),
  );
  expect(screen.getByText("This action cannot be undone.")).toBeTruthy();
  expect(
    screen.getByText(
      /Permanently delete 2 containers: OldEndpoint, OtherEndpoint/,
    ),
  ).toBeTruthy();
  expect(
    (
      screen.getByRole("button", {
        name: "Permanently delete 2 containers",
      }) as HTMLButtonElement
    ).disabled,
  ).toBe(true);
  await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
  expect(deleteMock).not.toHaveBeenCalled();
  await userEvent.click(
    screen.getByRole("button", { name: "Delete selected (2)" }),
  );
  await userEvent.type(
    screen.getByPlaceholderText("DELETE 2 CONTAINERS"),
    "DELETE 2 CONTAINERS",
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Permanently delete 2 containers" }),
  );
  await waitFor(() => expect(deleteMock).toHaveBeenCalledTimes(2));
  expect(
    deleteMock.mock.calls.map(([body, name]) => [body.confirmation, name]),
  ).toEqual([
    ["OldEndpoint", "OldEndpoint"],
    ["OtherEndpoint", "OtherEndpoint"],
  ]);
});

it("reports partial failures after refreshing and does not retry successful deletions", async () => {
  listMock
    .mockResolvedValueOnce(mixedContainers)
    .mockResolvedValue([mixedContainers[3]]);
  deleteMock
    .mockResolvedValueOnce({ deleted: true })
    .mockRejectedValueOnce(new Error("Unavailable"));
  await renderManager();
  await userEvent.click(
    screen.getByRole("checkbox", {
      name: "Select all visible non-platform containers",
    }),
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Delete selected (2)" }),
  );
  await userEvent.type(
    screen.getByPlaceholderText("DELETE 2 CONTAINERS"),
    "DELETE 2 CONTAINERS",
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Permanently delete 2 containers" }),
  );
  expect(await screen.findByRole("alert")).toHaveProperty(
    "textContent",
    "Deleted 1 of 2 containers. Could not delete: OtherEndpoint.",
  );
  expect(screen.queryByText("OldEndpoint")).toBeNull();
  expect(screen.getByText("OtherEndpoint")).toBeTruthy();
  expect(deleteMock).toHaveBeenCalledTimes(2);
});

it("clears selection on refresh and protects containers whose membership is unknown", async () => {
  listMock.mockResolvedValue([...mixedContainers, { name: "UnknownEndpoint" }]);
  await renderManager();
  expect(
    (
      screen.getByRole("checkbox", {
        name: "Select UnknownEndpoint",
      }) as HTMLInputElement
    ).disabled,
  ).toBe(true);
  await userEvent.click(
    screen.getByRole("checkbox", { name: "Select OldEndpoint" }),
  );
  expect(
    (
      screen.getByRole("checkbox", {
        name: "Select all visible non-platform containers",
      }) as HTMLInputElement
    ).indeterminate,
  ).toBe(true);
  await userEvent.click(screen.getByRole("button", { name: "Refresh" }));
  await waitFor(() => expect(listMock).toHaveBeenCalledTimes(2));
  expect(
    (
      screen.getByRole("button", {
        name: "Delete selected (0)",
      }) as HTMLButtonElement
    ).disabled,
  ).toBe(true);
});

it("keeps the batch fixed and prevents dismissing or repeating an active deletion", async () => {
  listMock.mockResolvedValue(mixedContainers);
  let finish!: (value: { deleted: boolean }) => void;
  deleteMock.mockReturnValueOnce(
    new Promise((resolve) => {
      finish = resolve;
    }),
  );
  await renderManager();
  await userEvent.click(
    screen.getByRole("checkbox", { name: "Select OldEndpoint" }),
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Delete selected (1)" }),
  );
  await userEvent.type(
    screen.getByPlaceholderText("OldEndpoint"),
    "OldEndpoint",
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Permanently delete container" }),
  );
  expect(
    (screen.getByRole("button", { name: "Refresh" }) as HTMLButtonElement)
      .disabled,
  ).toBe(true);
  expect(
    (screen.getByLabelText("Platform membership") as HTMLSelectElement)
      .disabled,
  ).toBe(true);
  expect(
    (screen.getByRole("button", { name: "Cancel" }) as HTMLButtonElement)
      .disabled,
  ).toBe(true);
  await userEvent.keyboard("{Escape}");
  expect(screen.getByRole("dialog")).toBeTruthy();
  expect(deleteMock).toHaveBeenCalledTimes(1);
  await act(async () => {
    finish({ deleted: true });
  });
  await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
});
