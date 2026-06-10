import { act, renderHook, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { useUrlFilters } from "./use-url-filters";

type Params = { status: string[]; q: string };
const defaults: Params = { status: ["A"], q: "" };

function wrapperAt(entry: string) {
  return ({ children }: { children: React.ReactNode }) => (
    <MemoryRouter initialEntries={[entry]}>{children}</MemoryRouter>
  );
}

describe("useUrlFilters sessionStorage persistence", () => {
  beforeEach(() => sessionStorage.clear());
  afterEach(() => sessionStorage.clear());

  it("mirrors applied filters into sessionStorage on change", () => {
    const { result } = renderHook(
      () => useUrlFilters(defaults, { persistKey: "k" }),
      { wrapper: wrapperAt("/") },
    );

    act(() => result.current.applyFilters({ status: ["B"], q: "x" }));

    const saved = JSON.parse(sessionStorage.getItem("k")!);
    expect(saved).toMatchObject({ status: ["B"], q: "x" });
  });

  it("restores from sessionStorage when the URL has no owned params", async () => {
    sessionStorage.setItem("k", JSON.stringify({ status: ["B"], q: "x" }));

    const { result } = renderHook(
      () => useUrlFilters(defaults, { persistKey: "k" }),
      { wrapper: wrapperAt("/") },
    );

    await waitFor(() => expect(result.current.applied.status).toEqual(["B"]));
    expect(result.current.applied.q).toBe("x");
  });

  it("lets the URL win over sessionStorage", async () => {
    sessionStorage.setItem("k", JSON.stringify({ status: ["B"] }));

    const { result } = renderHook(
      () => useUrlFilters(defaults, { persistKey: "k" }),
      { wrapper: wrapperAt("/?status=C") },
    );

    // Give any (suppressed) hydrate effect a tick; URL must remain authoritative.
    await Promise.resolve();
    expect(result.current.applied.status).toEqual(["C"]);
  });

  it("clears sessionStorage on reset", () => {
    sessionStorage.setItem("k", JSON.stringify({ status: ["B"] }));

    const { result } = renderHook(
      () => useUrlFilters(defaults, { persistKey: "k" }),
      { wrapper: wrapperAt("/?status=B") },
    );

    act(() => result.current.resetFilters());

    expect(sessionStorage.getItem("k")).toBeNull();
  });

  it("does not touch sessionStorage when no persistKey is given", () => {
    const { result } = renderHook(() => useUrlFilters(defaults), {
      wrapper: wrapperAt("/"),
    });

    act(() => result.current.applyFilters({ status: ["B"] }));

    expect(sessionStorage.length).toBe(0);
  });
});
