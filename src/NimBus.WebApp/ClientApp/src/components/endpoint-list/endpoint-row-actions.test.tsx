import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import * as api from "api-client";
import { ToastProvider } from "components/ui";
import EndpointRowActions from "./endpoint-row-actions";

const apiMocks = vi.hoisted(() => ({
  postEndpointSubscriptionstatus: vi.fn(),
  postEndpointPurge: vi.fn(),
  getEndpointAccessControl: vi.fn(),
}));

vi.mock("api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("api-client")>();
  return {
    ...actual,
    CookieAuth: vi.fn(() => ({})),
    Client: vi.fn(function () {
      return apiMocks;
    }),
  };
});

// The row derives what it may offer from the current user's resolved access —
// stub it rather than the /accesscontrol/me round trip behind it.
const access = vi.hoisted(() => ({ current: null as unknown }));
vi.mock("hooks/use-access", async (importOriginal) => ({
  // Keep the real isOwnerRole — the casing it tolerates is part of what these
  // tests cover. Only the network-backed hook is stubbed.
  ...(await importOriginal<typeof import("hooks/use-access")>()),
  useAccess: () => ({ access: access.current }),
  invalidateAccess: vi.fn(),
}));

// Role values are spelled the way the SERVER sends them — PascalCase CLR names
// ("Owner"), not the lowercase values the OpenAPI spec and generated client
// declare. Verified live against /api/access-control/me. Keep these as-is: they
// are the regression guard for that mismatch.
const asOwnerOfAlice = () => {
  access.current = {
    siteRole: "Contributor",
    endpointRoles: [{ endpointId: "Alice", role: "Owner" }],
  };
};

const asSiteOwner = () => {
  access.current = { siteRole: "Owner", endpointRoles: [] };
};

const asReader = () => {
  access.current = {
    siteRole: "Reader",
    endpointRoles: [{ endpointId: "Alice", role: "Reader" }],
  };
};

const refreshEndpoint = vi.fn();

const renderActions = (subscriptionStatus = "active", env = "dev") =>
  render(
    <MemoryRouter>
      <ToastProvider>
        <EndpointRowActions
          endpointId="Alice"
          subscriptionStatus={subscriptionStatus}
          failed={2}
          deferred={0}
          pending={7}
          storageAvailable
          env={env}
          refreshEndpoint={refreshEndpoint}
          startLoading={vi.fn()}
          stopLoading={vi.fn()}
        />
      </ToastProvider>
    </MemoryRouter>,
  );

const openMenu = () =>
  userEvent.click(
    screen.getByRole("button", { name: /more actions for alice/i }),
  );

describe("EndpointRowActions", () => {
  beforeEach(() => {
    asOwnerOfAlice();
    apiMocks.postEndpointSubscriptionstatus.mockResolvedValue(undefined);
    apiMocks.postEndpointPurge.mockResolvedValue(undefined);
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it("offers the owner-only items to an endpoint owner", async () => {
    renderActions();
    await openMenu();

    expect(screen.getByRole("menuitem", { name: /configure alerts/i })).toBeTruthy();
    expect(screen.getByRole("menuitem", { name: /open endpoint/i })).toBeTruthy();
    expect(screen.getByRole("menuitem", { name: /manage access/i })).toBeTruthy();
    expect(screen.getByRole("menuitem", { name: /purge data/i })).toBeTruthy();
    expect(
      screen.getByRole("switch", { name: /disable alice/i }).hasAttribute("disabled"),
    ).toBe(false);
  });

  it("recognises an owner grant whose endpoint id differs only by case", async () => {
    // Endpoint ids are matched case-insensitively server side, so a grant stored
    // as "alice" authorizes requests against "Alice" — the row must not hide the
    // controls the server would honour.
    access.current = {
      siteRole: "Reader",
      endpointRoles: [{ endpointId: "alice", role: "Owner" }],
    };
    renderActions();
    await openMenu();

    expect(screen.getByRole("menuitem", { name: /manage access/i })).toBeTruthy();
    expect(
      screen.getByRole("switch", { name: /disable alice/i }).hasAttribute("disabled"),
    ).toBe(false);
  });

  it("stops loading and reports when the row cannot be refreshed", async () => {
    // The disable already landed; only the read-back failed. Leaving that
    // rejection unhandled would strand the table in its loading state.
    const stopLoading = vi.fn();
    render(
      <MemoryRouter>
        <ToastProvider>
          <EndpointRowActions
            endpointId="Alice"
            subscriptionStatus="active"
            failed={0}
            deferred={0}
            pending={0}
            storageAvailable
            env="dev"
            refreshEndpoint={() => Promise.reject(new Error("boom"))}
            startLoading={vi.fn()}
            stopLoading={stopLoading}
          />
        </ToastProvider>
      </MemoryRouter>,
    );

    await userEvent.click(screen.getByRole("switch", { name: /disable alice/i }));
    await userEvent.click(screen.getByRole("button", { name: /disable endpoint/i }));

    expect(apiMocks.postEndpointSubscriptionstatus).toHaveBeenCalledWith(
      "Alice",
      "disable",
    );
    await waitFor(() => expect(stopLoading).toHaveBeenCalled());
    expect(
      await screen.findByText(/could not be refreshed/i),
    ).toBeTruthy();
  });

  it("hides every mutating action from a reader and locks the switch", async () => {
    asReader();
    renderActions();
    await openMenu();

    expect(screen.getByRole("menuitem", { name: /open endpoint/i })).toBeTruthy();
    expect(screen.queryByRole("menuitem", { name: /configure alerts/i })).toBeNull();
    expect(screen.queryByRole("menuitem", { name: /manage access/i })).toBeNull();
    expect(screen.queryByRole("menuitem", { name: /purge data/i })).toBeNull();
    expect(
      screen.getByRole("switch", { name: /disable alice/i }).hasAttribute("disabled"),
    ).toBe(true);
  });

  it("keeps purge out of the menu in a protected environment", async () => {
    renderActions("active", "prod");
    await openMenu();

    expect(screen.queryByRole("menuitem", { name: /purge data/i })).toBeNull();
  });

  it("still offers purge in a protected environment to a site owner", async () => {
    asSiteOwner();
    renderActions("active", "prod");
    await openMenu();

    // The server lets a site Owner purge anywhere, so the item stays — without
    // the "dev only" hint, which would be a lie here.
    const purge = screen.getByRole("menuitem", { name: /purge data/i });
    expect(purge.textContent).not.toContain("dev only");
  });

  it("confirms before disabling, then posts disable once", async () => {
    renderActions();

    await userEvent.click(screen.getByRole("switch", { name: /disable alice/i }));
    // Nothing is sent until the impact dialog is acknowledged.
    expect(apiMocks.postEndpointSubscriptionstatus).not.toHaveBeenCalled();

    await userEvent.click(screen.getByRole("button", { name: /disable endpoint/i }));

    expect(apiMocks.postEndpointSubscriptionstatus).toHaveBeenCalledTimes(1);
    expect(apiMocks.postEndpointSubscriptionstatus).toHaveBeenCalledWith(
      "Alice",
      "disable",
    );
    expect(refreshEndpoint).toHaveBeenCalledWith("Alice");
  });

  it("re-enables immediately, with no confirmation", async () => {
    renderActions("disabled");

    await userEvent.click(screen.getByRole("switch", { name: /enable alice/i }));

    expect(apiMocks.postEndpointSubscriptionstatus).toHaveBeenCalledWith(
      "Alice",
      "enable",
    );
  });

  it("leaves the switch dead when the subscription is missing", async () => {
    renderActions("not-found");

    const toggle = screen.getByRole("switch", { name: /enable alice/i });
    expect(toggle.hasAttribute("disabled")).toBe(true);
    await userEvent.click(toggle);
    expect(apiMocks.postEndpointSubscriptionstatus).not.toHaveBeenCalled();
  });

  it("arms purge only once the endpoint name is typed exactly", async () => {
    renderActions();
    await openMenu();
    await userEvent.click(screen.getByRole("menuitem", { name: /purge data/i }));

    const confirmButton = screen.getByRole("button", { name: /^purge data$/i });
    expect(confirmButton.hasAttribute("disabled")).toBe(true);

    const input = screen.getByLabelText(/to confirm/i);
    await userEvent.type(input, "alice");
    expect(confirmButton.hasAttribute("disabled")).toBe(true);

    await userEvent.clear(input);
    await userEvent.type(input, "Alice");
    expect(confirmButton.hasAttribute("disabled")).toBe(false);

    await userEvent.click(confirmButton);
    expect(apiMocks.postEndpointPurge).toHaveBeenCalledWith("Alice");
  });
});
