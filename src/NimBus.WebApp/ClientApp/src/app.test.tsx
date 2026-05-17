import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import App from "./app";
import { beforeEach, describe, expect, it, vi } from "vitest";

describe("App", () => {
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
});
