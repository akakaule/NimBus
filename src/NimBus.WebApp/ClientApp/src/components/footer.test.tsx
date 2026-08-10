import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import {
  EMPTY_STATUS,
  FULL_STATUS,
  settleAppStatus,
  stubStatusFetch,
} from "../test-utils/stub-app-status";

// AC4: after signing in, the footer must still show the platform version and the
// "store: <provider>" badge — GH#93 only trims what an ANONYMOUS caller sees.
// The anonymous case is the negative half of the same pair, asserted after the
// response has been parsed and applied so it is a real regression guard rather
// than a snapshot of the hooks' initial undefined.
describe("Footer", () => {
  beforeEach(() => {
    vi.resetModules();
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("shows the platform version and storage-provider badge for an authenticated caller", async () => {
    const fetchMock = stubStatusFetch(() => FULL_STATUS);
    // Import the hooks module by the alias the component itself uses, so the
    // test drives the same module instance.
    const hooks = await import("hooks/app-status");
    const { default: Footer } = await import("components/footer");

    render(<Footer />);
    await settleAppStatus(hooks);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(screen.getByText(FULL_STATUS.platformVersion)).toBeTruthy();
    expect(screen.getByText(/store:\s*Cosmos DB/)).toBeTruthy();
  });

  it("renders without version or provider for the anonymous body", async () => {
    const fetchMock = stubStatusFetch(() => EMPTY_STATUS);
    const hooks = await import("hooks/app-status");
    const { default: Footer } = await import("components/footer");

    render(<Footer />);
    await settleAppStatus(hooks);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(screen.queryByText(FULL_STATUS.platformVersion)).toBeNull();
    expect(screen.queryByText(/store:/)).toBeNull();
    // The static half still renders — proof the component did not blow up.
    expect(screen.getByText(/NimBus/)).toBeTruthy();
  });
});
