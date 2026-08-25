import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import * as api from "api-client";

const mocks = vi.hoisted(() => {
  interface PendingRequest {
    endpointId: string;
    resolve: (value: unknown) => void;
  }

  const pendingLoads: PendingRequest[] = [];
  const pendingAdds: PendingRequest[] = [];

  const defer = (queue: PendingRequest[], endpointId: string) =>
    new Promise<unknown>((resolve) => {
      queue.push({ endpointId, resolve });
    });

  return {
    access: {
      canManageAccessControl: false,
      endpointRoles: [
        { endpointId: "endpoint-a", role: "owner" },
        { endpointId: "endpoint-b", role: "owner" },
      ],
    },
    pendingLoads,
    pendingAdds,
    getEndpointAccessControl: vi.fn((endpointId: string) =>
      defer(pendingLoads, endpointId),
    ),
    postEndpointAccessControlRole: vi.fn(
      (endpointId: string, _entry: unknown) => defer(pendingAdds, endpointId),
    ),
    deleteEndpointAccessControlRole: vi.fn(),
    invalidateAccess: vi.fn(),
  };
});

vi.mock("hooks/use-access", async (importOriginal) => ({
  // Only the network-backed hook is stubbed; isOwnerRole stays real so the page
  // resolves endpoint ownership the same way it does against a live server.
  ...(await importOriginal<typeof import("hooks/use-access")>()),
  useAccess: () => ({ access: mocks.access }),
  invalidateAccess: mocks.invalidateAccess,
}));

vi.mock("components/ui/toast", () => ({
  useToast: () => ({ addToast: vi.fn() }),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") =
    await vi.importActual("api-client");

  class FakeClient {
    getEndpointAccessControl = mocks.getEndpointAccessControl;
    postEndpointAccessControlRole = mocks.postEndpointAccessControlRole;
    deleteEndpointAccessControlRole = mocks.deleteEndpointAccessControlRole;
  }

  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

const accessSet = (reader: string) =>
  new api.AccessControlSet({ readers: [reader] });

const renderPage = async () => {
  const { default: AccessControl } = await import("./access-control");
  render(
    <MemoryRouter>
      <AccessControl />
    </MemoryRouter>,
  );
};

const selectEndpoint = (endpointId: string) => {
  fireEvent.change(screen.getByLabelText("Endpoint"), {
    target: { value: endpointId },
  });
};

const resolveLoad = async (endpointId: string, reader: string) => {
  const request = mocks.pendingLoads.find(
    (pending) => pending.endpointId === endpointId,
  );
  expect(request).toBeDefined();
  await act(async () => {
    request!.resolve(accessSet(reader));
  });
};

beforeEach(() => {
  mocks.pendingLoads.length = 0;
  mocks.pendingAdds.length = 0;
  mocks.getEndpointAccessControl.mockClear();
  mocks.postEndpointAccessControlRole.mockClear();
  mocks.deleteEndpointAccessControlRole.mockReset();
  mocks.invalidateAccess.mockClear();
});

afterEach(() => {
  cleanup();
});

describe("AccessControl endpoint race safety", () => {
  it("drops endpoint A's slow load response after endpoint B is selected", async () => {
    await renderPage();

    selectEndpoint("endpoint-a");
    await waitFor(() =>
      expect(mocks.getEndpointAccessControl).toHaveBeenCalledWith("endpoint-a"),
    );

    selectEndpoint("endpoint-b");
    await waitFor(() =>
      expect(mocks.getEndpointAccessControl).toHaveBeenCalledWith("endpoint-b"),
    );

    await resolveLoad("endpoint-b", "reader-b@example.test");
    await resolveLoad("endpoint-a", "reader-a@example.test");

    expect(screen.getByText("reader-b@example.test")).toBeTruthy();
    expect(screen.queryByText("reader-a@example.test")).toBeNull();
  });

  it("does not commit endpoint A's mutation response after switching to endpoint B", async () => {
    await renderPage();

    selectEndpoint("endpoint-a");
    await waitFor(() =>
      expect(mocks.getEndpointAccessControl).toHaveBeenCalledWith("endpoint-a"),
    );
    await resolveLoad("endpoint-a", "reader-a@example.test");

    fireEvent.change(screen.getByLabelText("Add entry to Readers"), {
      target: { value: "new-reader-a@example.test" },
    });
    fireEvent.click(screen.getAllByRole("button", { name: "Add" })[0]);

    await waitFor(() =>
      expect(mocks.postEndpointAccessControlRole).toHaveBeenCalledTimes(1),
    );
    expect(mocks.pendingAdds[0].endpointId).toBe("endpoint-a");

    selectEndpoint("endpoint-b");
    await waitFor(() =>
      expect(mocks.getEndpointAccessControl).toHaveBeenCalledWith("endpoint-b"),
    );
    await resolveLoad("endpoint-b", "reader-b@example.test");

    await act(async () => {
      mocks.pendingAdds[0].resolve(accessSet("new-reader-a@example.test"));
    });

    expect(screen.getByText("reader-b@example.test")).toBeTruthy();
    expect(screen.queryByText("new-reader-a@example.test")).toBeNull();
  });

  it("does not expose endpoint A's stale entries as mutations against endpoint B", async () => {
    mocks.deleteEndpointAccessControlRole.mockResolvedValue(
      accessSet("reader-b@example.test"),
    );
    await renderPage();

    selectEndpoint("endpoint-a");
    await waitFor(() =>
      expect(mocks.getEndpointAccessControl).toHaveBeenCalledWith("endpoint-a"),
    );
    await resolveLoad("endpoint-a", "reader-a@example.test");

    selectEndpoint("endpoint-b");

    const staleRemove = screen.queryByRole("button", {
      name: "Remove reader-a@example.test from Readers",
    });
    if (staleRemove) fireEvent.click(staleRemove);

    expect(mocks.deleteEndpointAccessControlRole).not.toHaveBeenCalled();
    expect(screen.queryByText("reader-a@example.test")).toBeNull();
  });

  it("keeps a refreshed endpoint A load over an older endpoint A mutation", async () => {
    await renderPage();

    selectEndpoint("endpoint-a");
    await waitFor(() =>
      expect(mocks.getEndpointAccessControl).toHaveBeenCalledWith("endpoint-a"),
    );
    await resolveLoad("endpoint-a", "reader-a@example.test");

    fireEvent.change(screen.getByLabelText("Add entry to Readers"), {
      target: { value: "old-mutation@example.test" },
    });
    fireEvent.click(screen.getAllByRole("button", { name: "Add" })[0]);
    await waitFor(() =>
      expect(mocks.postEndpointAccessControlRole).toHaveBeenCalledTimes(1),
    );

    selectEndpoint("endpoint-b");
    await waitFor(() =>
      expect(mocks.getEndpointAccessControl).toHaveBeenCalledWith("endpoint-b"),
    );
    selectEndpoint("endpoint-a");
    await waitFor(() =>
      expect(mocks.getEndpointAccessControl).toHaveBeenCalledTimes(3),
    );

    const refreshedLoad = mocks.pendingLoads[mocks.pendingLoads.length - 1];
    expect(refreshedLoad.endpointId).toBe("endpoint-a");
    await act(async () => {
      refreshedLoad.resolve(accessSet("fresh-reader-a@example.test"));
    });
    await act(async () => {
      mocks.pendingAdds[0].resolve(accessSet("old-mutation@example.test"));
    });

    expect(screen.getByText("fresh-reader-a@example.test")).toBeTruthy();
    expect(screen.queryByText("old-mutation@example.test")).toBeNull();
  });
});
