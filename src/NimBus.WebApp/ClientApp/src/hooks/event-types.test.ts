import { describe, it, expect, vi, beforeEach } from "vitest";

// The hook talks to the NSwag-generated api-client Client. We replace the
// Client class so we can drive getEventtypesByEndpointId per call.
// Note: vi.mock is hoisted; using vi.hoisted lets us share the mock fn
// across the factory body and the test assertions.
const { getEventtypesByEndpointIdMock } = vi.hoisted(() => ({
  getEventtypesByEndpointIdMock: vi.fn(),
}));
vi.mock("api-client", () => {
  class MockClient {
    getEventtypesByEndpointId = getEventtypesByEndpointIdMock;
  }
  return { Client: MockClient, CookieAuth: () => ({}) };
});

describe("getEventTypesByEndpoint request caching", () => {
  beforeEach(() => {
    // The module keeps cache/pending maps at module scope; reset the module so
    // each test starts with empty maps.
    vi.resetModules();
    getEventtypesByEndpointIdMock.mockReset();
  });

  it("does not cache a rejected request and retries on the next call", async () => {
    getEventtypesByEndpointIdMock
      .mockRejectedValueOnce(new Error("boom"))
      .mockResolvedValueOnce({ eventTypes: ["ok"] });

    const { getEventTypesByEndpoint } = await import("./event-types");

    // First call fails.
    await expect(getEventTypesByEndpoint("ep-1")).rejects.toThrow("boom");
    expect(getEventtypesByEndpointIdMock).toHaveBeenCalledTimes(1);

    // Second call must issue a NEW request (the rejection was not cached).
    await expect(getEventTypesByEndpoint("ep-1")).resolves.toEqual({
      eventTypes: ["ok"],
    });
    expect(getEventtypesByEndpointIdMock).toHaveBeenCalledTimes(2);
  });

  it("caches a successful result and does not re-request", async () => {
    getEventtypesByEndpointIdMock.mockResolvedValue({ eventTypes: ["ok"] });

    const { getEventTypesByEndpoint } = await import("./event-types");

    await expect(getEventTypesByEndpoint("ep-1")).resolves.toEqual({
      eventTypes: ["ok"],
    });
    await expect(getEventTypesByEndpoint("ep-1")).resolves.toEqual({
      eventTypes: ["ok"],
    });
    expect(getEventtypesByEndpointIdMock).toHaveBeenCalledTimes(1);
  });

  it("dedupes concurrent requests into a single underlying call", async () => {
    let resolveRequest!: (value: unknown) => void;
    getEventtypesByEndpointIdMock.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolveRequest = resolve;
        }),
    );

    const { getEventTypesByEndpoint } = await import("./event-types");

    const first = getEventTypesByEndpoint("ep-2");
    const second = getEventTypesByEndpoint("ep-2");
    resolveRequest({ eventTypes: ["shared"] });

    await expect(first).resolves.toEqual({ eventTypes: ["shared"] });
    await expect(second).resolves.toEqual({ eventTypes: ["shared"] });
    expect(getEventtypesByEndpointIdMock).toHaveBeenCalledTimes(1);
  });
});
