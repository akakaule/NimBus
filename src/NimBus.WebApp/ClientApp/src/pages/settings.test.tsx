import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import Settings from "./settings";

vi.mock("components/admin/mcp-access-settings", () => ({
  default: () => <div>MCP access settings</div>,
}));
vi.mock("components/admin/failure-intelligence-settings", () => ({
  default: () => <div>Failure intelligence settings</div>,
}));
vi.mock("components/admin/heartbeat-card", () => ({
  default: () => <div>Heartbeat probing settings</div>,
}));
vi.mock("components/admin/simulation-settings", () => ({
  default: () => <div>Simulation settings</div>,
}));
vi.mock("components/admin/audit-settings", () => ({
  default: () => <div>Audit settings</div>,
}));

function Location() {
  return <output aria-label="location">{useLocation().pathname}</output>;
}

function renderAt(...entries: string[]) {
  return render(
    <MemoryRouter initialEntries={entries} initialIndex={entries.length - 1}>
      <Routes>
        <Route path="/Settings/:feature?" element={<Settings />} />
      </Routes>
      <Location />
    </MemoryRouter>,
  );
}

const location = () => screen.getByLabelText("location").textContent;

afterEach(() => cleanup());

describe("Settings page", () => {
  it("lists every opt-in feature, grouped, with no panel open", () => {
    renderAt("/Settings");

    expect(screen.getByRole("heading", { name: "Settings" })).toBeTruthy();
    for (const group of ["AI & agents", "Monitoring", "Testing", "Compliance"]) {
      expect(screen.getByRole("heading", { name: group })).toBeTruthy();
    }
    const links = {
      "MCP access": "/Settings/mcp",
      "Failure intelligence": "/Settings/failure-intelligence",
      "Heartbeat probing": "/Settings/heartbeat",
      Simulation: "/Settings/simulation",
      "Audit logging": "/Settings/audit",
    };
    for (const [name, href] of Object.entries(links)) {
      expect(
        screen.getByRole("link", { name: new RegExp(name) }).getAttribute("href"),
      ).toBe(href);
    }
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("opens a feature's existing settings in a side panel from its row", () => {
    renderAt("/Settings");

    fireEvent.click(screen.getByRole("link", { name: /MCP access/ }));

    expect(location()).toBe("/Settings/mcp");
    const dialog = screen.getByRole("dialog", { name: "MCP access" });
    expect(dialog.textContent).toContain("MCP access settings");
  });

  it("deep-links to a feature's panel", () => {
    renderAt("/Settings/failure-intelligence");

    expect(
      screen.getByRole("dialog", { name: "Failure intelligence" }).textContent,
    ).toContain("Failure intelligence settings");
  });

  it("closes the panel back to the list", () => {
    renderAt("/Settings/audit");

    fireEvent.click(screen.getByRole("button", { name: "Close Audit logging" }));

    expect(location()).toBe("/Settings");
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("closes with Escape", () => {
    renderAt("/Settings/heartbeat");

    fireEvent.keyDown(document, { key: "Escape" });

    expect(location()).toBe("/Settings");
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("returns through history when the panel was opened from the list, so Back does not reopen it", () => {
    renderAt("/Settings");
    fireEvent.click(screen.getByRole("link", { name: /Simulation/ }));

    fireEvent.click(screen.getByRole("button", { name: "Close Simulation" }));

    expect(location()).toBe("/Settings");
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("sends an unknown feature back to the list", () => {
    renderAt("/Settings/nope");

    expect(location()).toBe("/Settings");
    expect(screen.queryByRole("dialog")).toBeNull();
  });
});
