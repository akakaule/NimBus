import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

const { storageProviderMock } = vi.hoisted(() => ({
  storageProviderMock: vi.fn(),
}));

vi.mock("hooks/app-status", () => ({
  useStorageProvider: storageProviderMock,
}));
vi.mock("components/page", () => ({
  default: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));
vi.mock("components/admin/topology", () => ({ default: () => null }));
vi.mock("components/admin/operations", () => ({ default: () => null }));
vi.mock("components/admin/subscription-manager", () => ({
  default: () => null,
}));
vi.mock("components/admin/health", () => ({ default: () => null }));
vi.mock("components/admin/cosmos-container-manager", () => ({
  default: () => <div>Cosmos container manager</div>,
}));

afterEach(() => {
  cleanup();
  vi.resetAllMocks();
});

describe("Admin storage tab", () => {
  it("is available for Cosmos DB storage", async () => {
    storageProviderMock.mockReturnValue("Cosmos DB");
    const { default: Admin } = await import("./admin");

    render(<Admin />);

    expect(screen.getByRole("tab", { name: "Storage" })).toBeTruthy();
  });

  it("is not available for SQL Server storage", async () => {
    storageProviderMock.mockReturnValue("SQL Server");
    const { default: Admin } = await import("./admin");

    render(<Admin />);

    expect(screen.queryByRole("tab", { name: "Storage" })).toBeNull();
    expect(screen.queryByText("Cosmos container manager")).toBeNull();
  });
});
