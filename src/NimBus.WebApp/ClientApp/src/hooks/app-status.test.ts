import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, renderHook } from "@testing-library/react";
import { ApplicationStatus } from "api-client";
import {
  EMPTY_STATUS,
  FULL_STATUS,
  settleAppStatus,
  stubStatusFetch,
} from "../test-utils/stub-app-status";

// GH#93: /api/app/stats answers anonymous callers with an empty status object,
// so the hooks that feed the sidebar badge, the footer and the ticket deep link
// must tolerate every field being null/absent — and must not pin that empty
// answer in the module-level cache.
describe("app-status hooks", () => {
  beforeEach(() => {
    vi.resetModules();
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("resolves every hook to a falsy value for the anonymous (trimmed) body", async () => {
    const fetchMock = stubStatusFetch(() => EMPTY_STATUS);
    const mod = await import("hooks/app-status");

    const env = renderHook(() => mod.useEnv());
    const version = renderHook(() => mod.usePlatformVersion());
    const provider = renderHook(() => mod.useStorageProvider());
    const template = renderHook(() => mod.useTicketLinkTemplate());

    await settleAppStatus(mod);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(env.result.current).toBeFalsy();
    expect(version.result.current).toBeFalsy();
    expect(provider.result.current).toBeFalsy();
    expect(template.result.current).toBeFalsy();

    // Completion proof: a real response body really was parsed into the
    // generated DTO — the four keys carry the response's explicit nulls, which
    // an unfetched/unparsed status could not produce. So the falsy assertions
    // above ran against a delivered answer, not the hooks' initial undefined.
    const status = await mod.getApplicationStatus();
    expect(status.env).toBeNull();
    expect(status.platformVersion).toBeNull();
    expect(status.storageProvider).toBeNull();
    expect(status.ticketLinkTemplate).toBeNull();
  });

  it("resolves every hook to its value for the authenticated (full) body", async () => {
    const fetchMock = stubStatusFetch(() => FULL_STATUS);
    const mod = await import("hooks/app-status");

    const env = renderHook(() => mod.useEnv());
    const version = renderHook(() => mod.usePlatformVersion());
    const provider = renderHook(() => mod.useStorageProvider());
    const template = renderHook(() => mod.useTicketLinkTemplate());

    await settleAppStatus(mod);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(env.result.current).toBe(FULL_STATUS.env);
    expect(version.result.current).toBe(FULL_STATUS.platformVersion);
    expect(provider.result.current).toBe(FULL_STATUS.storageProvider);
    expect(template.result.current).toBe(FULL_STATUS.ticketLinkTemplate);
  });

  it("does not cache an empty status and re-requests on the next call", async () => {
    // One module instance, no resetModules in between — this is the guard for
    // hooks/app-status.ts: an anonymous empty status must not pin itself in
    // cachedStatus for the page's lifetime.
    let next: unknown = EMPTY_STATUS;
    const fetchMock = stubStatusFetch(() => next);
    const mod = await import("hooks/app-status");

    const first = await mod.getApplicationStatus();
    expect(first.env).toBeFalsy();
    expect(fetchMock).toHaveBeenCalledTimes(1);

    next = FULL_STATUS; // the caller has since signed in
    const second = await mod.getApplicationStatus();
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(second.env).toBe(FULL_STATUS.env);
  });

  it("still caches a populated status and does not re-request", async () => {
    // The mirror of the test above: the guard narrows caching to empty
    // responses; it must not disable caching outright.
    const fetchMock = stubStatusFetch(() => FULL_STATUS);
    const mod = await import("hooks/app-status");

    const first = await mod.getApplicationStatus();
    const second = await mod.getApplicationStatus();

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(first.storageProvider).toBe(FULL_STATUS.storageProvider);
    expect(second.storageProvider).toBe(FULL_STATUS.storageProvider);
  });
});

// AC6: the generated TypeScript client must handle a response with all four
// fields missing (or explicitly null, which is what System.Text.Json emits)
// without a runtime error. Nothing is mocked here, so this is the real DTO.
describe("generated ApplicationStatus DTO", () => {
  it("accepts a body with every field absent", () => {
    const status = ApplicationStatus.fromJS({});
    expect(status.env).toBeUndefined();
    expect(status.platformVersion).toBeUndefined();
    expect(status.storageProvider).toBeUndefined();
    expect(status.ticketLinkTemplate).toBeUndefined();
  });

  it("accepts a body with every field explicitly null", () => {
    const status = ApplicationStatus.fromJS(EMPTY_STATUS);
    expect(status.env).toBeNull();
    expect(status.platformVersion).toBeNull();
    expect(status.storageProvider).toBeNull();
    expect(status.ticketLinkTemplate).toBeNull();
  });
});
