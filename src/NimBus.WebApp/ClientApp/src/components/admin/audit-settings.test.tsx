import { describe, it, expect, afterEach, beforeEach, vi } from "vitest";
import {
  cleanup,
  render,
  screen,
  fireEvent,
  waitFor,
  within,
} from "@testing-library/react";
import AuditSettings from "./audit-settings";

const mocks = vi.hoisted(() => ({
  getAdminAuditSettings: vi.fn(),
  putAdminAuditSettings: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") =
    await vi.importActual("api-client");
  class FakeClient {
    getAdminAuditSettings = mocks.getAdminAuditSettings;
    putAdminAuditSettings = mocks.putAdminAuditSettings;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

const configurable = [
  "searchEvents",
  "getEventDetails",
  "resubmit",
  "failureClassified",
  "someFutureType",
];

beforeEach(() => {
  mocks.getAdminAuditSettings.mockReset().mockResolvedValue({
    disabledAuditTypes: ["getEventDetails"],
    configurableAuditTypes: configurable,
  });
  mocks.putAdminAuditSettings
    .mockReset()
    .mockImplementation((body: { disabledAuditTypes: string[] }) =>
      Promise.resolve({
        disabledAuditTypes: body.disabledAuditTypes,
        configurableAuditTypes: configurable,
      }),
    );
});

afterEach(() => {
  cleanup();
});

describe("AuditSettings", () => {
  it("lists every configurable type with its recorded state", async () => {
    render(<AuditSettings />);

    const search = await screen.findByRole("switch", {
      name: "Record Search events",
    });
    expect(search.getAttribute("aria-checked")).toBe("true");
    expect(
      screen
        .getByRole("switch", { name: "Record Get event details" })
        .getAttribute("aria-checked"),
    ).toBe("false");
    // A type the UI does not group yet still shows up rather than vanishing.
    expect(
      within(screen.getByRole("region", { name: "Other" })).getByText(
        "Some future type",
      ),
    ).toBeTruthy();
    expect(screen.getByText("4 of 5 action types recorded.")).toBeTruthy();
  });

  it("saves the disabled types and only enables Save when something changed", async () => {
    render(<AuditSettings />);

    const save = await screen.findByRole("button", { name: "Save" });
    expect((save as HTMLButtonElement).disabled).toBe(true);

    fireEvent.click(screen.getByRole("switch", { name: "Record Search events" }));
    expect((save as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(save);

    await waitFor(() => expect(mocks.putAdminAuditSettings).toHaveBeenCalled());
    const body = mocks.putAdminAuditSettings.mock.calls[0][0];
    expect([...body.disabledAuditTypes].sort()).toEqual([
      "getEventDetails",
      "searchEvents",
    ]);
    await waitFor(() =>
      expect((save as HTMLButtonElement).disabled).toBe(true),
    );
  });

  it("switches a whole group with Record none", async () => {
    render(<AuditSettings />);

    const browsing = await screen.findByRole("region", { name: "Browsing" });
    // Browsing has one type off, so its group action offers Record all.
    fireEvent.click(within(browsing).getByRole("button", { name: "Record all" }));
    fireEvent.click(within(browsing).getByRole("button", { name: "Record none" }));

    for (const name of ["Record Search events", "Record Get event details"]) {
      expect(
        screen.getByRole("switch", { name }).getAttribute("aria-checked"),
      ).toBe("false");
    }
  });

  it("shows a load failure instead of an empty list", async () => {
    mocks.getAdminAuditSettings.mockReset().mockRejectedValue(new Error("Forbidden"));

    render(<AuditSettings />);

    expect((await screen.findByRole("alert")).textContent).toBe("Forbidden");
  });
});
