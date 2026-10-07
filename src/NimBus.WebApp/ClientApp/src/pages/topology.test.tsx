import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import Topology from "./topology";

const { storageProviderMock } = vi.hoisted(() => ({
  storageProviderMock: vi.fn(),
}));

vi.mock("hooks/app-status", () => ({
  useStorageProvider: storageProviderMock,
}));
vi.mock("components/admin/subscription-manager", () => ({
  default: () => <div>Subscription manager</div>,
}));
vi.mock("components/admin/topology-audit", () => ({
  default: () => <div>Topology audit</div>,
}));
vi.mock("components/admin/asyncapi-export", () => ({
  default: () => <div>AsyncAPI export</div>,
}));
vi.mock("components/admin/cosmos-container-manager", () => ({
  default: () => <div>Cosmos container manager</div>,
}));

function Location() {
  return <output aria-label="location">{useLocation().pathname}</output>;
}

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/Topology/:view?" element={<Topology />} />
      </Routes>
      <Location />
    </MemoryRouter>,
  );
}

const location = () => screen.getByLabelText("location").textContent;

afterEach(() => {
  cleanup();
  vi.resetAllMocks();
});

describe("Topology page", () => {
  it("opens on Subscriptions and offers the AsyncAPI export in the header", () => {
    storageProviderMock.mockReturnValue("SQL Server");
    renderAt("/Topology");

    expect(screen.getByRole("heading", { name: "Topology" })).toBeTruthy();
    expect(
      screen.getByRole("tab", { name: "Subscriptions" }).getAttribute("aria-selected"),
    ).toBe("true");
    expect(screen.getByText("Subscription manager")).toBeTruthy();
    expect(screen.getByText("AsyncAPI export")).toBeTruthy();
  });

  it("deep-links to Catalog drift", () => {
    storageProviderMock.mockReturnValue("SQL Server");
    renderAt("/Topology/drift");

    expect(
      screen.getByRole("tab", { name: "Catalog drift" }).getAttribute("aria-selected"),
    ).toBe("true");
    expect(screen.getByText("Topology audit")).toBeTruthy();
    expect(screen.queryByText("Subscription manager")).toBeNull();
  });

  it("gives each tab its own URL", () => {
    storageProviderMock.mockReturnValue("SQL Server");
    renderAt("/Topology");

    fireEvent.click(screen.getByRole("tab", { name: "Catalog drift" }));

    expect(location()).toBe("/Topology/drift");
    expect(screen.getByText("Topology audit")).toBeTruthy();
  });

  it("offers Storage for Cosmos DB", () => {
    storageProviderMock.mockReturnValue("Cosmos DB");
    renderAt("/Topology/storage");

    expect(
      screen.getByRole("tab", { name: "Storage" }).getAttribute("aria-selected"),
    ).toBe("true");
    expect(screen.getByText("Cosmos container manager")).toBeTruthy();
  });

  it("has no Storage view for SQL Server and sends its URL back to Subscriptions", () => {
    storageProviderMock.mockReturnValue("SQL Server");
    renderAt("/Topology/storage");

    expect(screen.queryByRole("tab", { name: "Storage" })).toBeNull();
    expect(screen.queryByText("Cosmos container manager")).toBeNull();
    expect(location()).toBe("/Topology");
  });

  it("waits for the storage provider before judging a Storage deep link", () => {
    storageProviderMock.mockReturnValue(undefined);
    renderAt("/Topology/storage");

    expect(location()).toBe("/Topology/storage");
  });

  it("sends an unknown view back to Subscriptions", () => {
    storageProviderMock.mockReturnValue("SQL Server");
    renderAt("/Topology/nope");

    expect(location()).toBe("/Topology");
  });
});
