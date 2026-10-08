import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import App from "./app";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

describe("App", () => {
  afterEach(() => cleanup());

  beforeEach(() => {
    Object.defineProperty(window, "matchMedia", {
      configurable: true,
      writable: true,
      value: vi.fn().mockImplementation((query: string) => ({
        matches: false,
        media: query,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      })),
    });
  });

  it("renders the application shell", () => {
    render(
      <MemoryRouter initialEntries={["/not-found"]}>
        <App />
      </MemoryRouter>,
    );

    expect(screen.getByRole("navigation")).toBeTruthy();
    expect(screen.getByText("NimBus")).toBeTruthy();
  });

  // Spec 038 §12.1: the Admin page is gone; old links and bookmarks land on Operations.
  it.each(["/Admin", "/admin"])("redirects %s to Operations", async (path) => {
    render(
      <MemoryRouter initialEntries={[path]}>
        <App />
      </MemoryRouter>,
    );

    expect(
      await screen.findByRole("heading", { level: 1, name: "Operations" }, { timeout: 5000 }),
    ).toBeTruthy();
  });

  // Spec 038 §5.2: each operation has its own URL.
  it("routes an operation's URL to the Operations page", async () => {
    render(
      <MemoryRouter initialEntries={["/Operations/dlq"]}>
        <App />
      </MemoryRouter>,
    );

    expect(
      await screen.findByRole("heading", { level: 1, name: "Operations" }, { timeout: 5000 }),
    ).toBeTruthy();
  });
});
