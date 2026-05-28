import { describe, it, expect, afterEach, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import moment from "moment";
import * as api from "api-client";
import MessageListing, {
  diffMs,
  getHistoryQueueTimeMs,
} from "./message-listing";

// MessageListing internally constructs an api.Client and queries `getMe()` on
// mount to label handoff actions. Stub the network call so the component test
// stays hermetic.
vi.mock("components/ui/toast", () => ({
  useToast: () => ({ addToast: () => {} }),
}));

const originalFetch = globalThis.fetch;
afterEach(() => {
  cleanup();
  globalThis.fetch = originalFetch;
});

function stubMe() {
  globalThis.fetch = vi.fn().mockResolvedValue(
    new Response(JSON.stringify({ name: "tester" }), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    }),
  ) as unknown as typeof fetch;
}

function msg(
  type: api.MessageType | string,
  timestampIso: string,
): api.Message {
  return {
    messageType: type as api.MessageType,
    enqueuedTimeUtc: moment(timestampIso),
  } as api.Message;
}

// ---------------------------------------------------------------------------
// FR-040 - unit tests for getHistoryQueueTimeMs (7 enumerated cases).
// ---------------------------------------------------------------------------
describe("getHistoryQueueTimeMs (spec 005)", () => {
  it("case 1 - returns undefined for empty / undefined messages", () => {
    expect(getHistoryQueueTimeMs(undefined)).toBeUndefined();
    expect(getHistoryQueueTimeMs(null as unknown as api.Message[])).toBeUndefined();
    expect(getHistoryQueueTimeMs([])).toBeUndefined();
  });

  it("case 2 - returns undefined for a lifecycle with no deferral or pending-handoff", () => {
    const history: api.Message[] = [
      msg(api.MessageType.EventRequest, "2026-05-28T10:00:00.000Z"),
      msg(api.MessageType.ResolutionResponse, "2026-05-28T10:00:00.045Z"),
    ];
    expect(getHistoryQueueTimeMs(history)).toBeUndefined();
  });

  it("case 3 - single deferral round-trip: span = latest end-anchor - first EventRequest", () => {
    const history: api.Message[] = [
      msg(api.MessageType.EventRequest, "2026-05-28T10:00:00.000Z"),
      msg(api.MessageType.DeferralResponse, "2026-05-28T10:00:00.500Z"),
      msg(api.MessageType.EventRequest, "2026-05-28T10:00:30.000Z"), // replay
      msg(api.MessageType.ResolutionResponse, "2026-05-28T10:00:30.050Z"),
    ];
    // 30,050 ms between first EventRequest and final ResolutionResponse.
    expect(getHistoryQueueTimeMs(history)).toBe(30_050);
  });

  it("case 4 - pending-handoff that resolves via HandoffCompletedRequest", () => {
    const history: api.Message[] = [
      msg(api.MessageType.EventRequest, "2026-05-28T11:00:00.000Z"),
      msg(api.MessageType.PendingHandoffResponse, "2026-05-28T11:00:00.040Z"),
      msg(api.MessageType.HandoffCompletedRequest, "2026-05-28T11:05:00.000Z"),
      msg(api.MessageType.ResolutionResponse, "2026-05-28T11:05:00.080Z"),
    ];
    // End anchor is the latest recognised lifecycle outcome -> ResolutionResponse.
    expect(getHistoryQueueTimeMs(history)).toBe(300_080);
  });

  it("case 5 - still-pending handoff: end anchor is the latest PendingHandoffResponse", () => {
    const history: api.Message[] = [
      msg(api.MessageType.EventRequest, "2026-05-28T12:00:00.000Z"),
      msg(api.MessageType.PendingHandoffResponse, "2026-05-28T12:02:00.000Z"),
    ];
    expect(getHistoryQueueTimeMs(history)).toBe(120_000);
  });

  it("case 6 - partial history (no EventRequest) falls back to earliest message as start", () => {
    const history: api.Message[] = [
      // Aged out: EventRequest missing. Start anchor falls back to earliest
      // remaining message per FR-006.
      msg(api.MessageType.DeferralResponse, "2026-05-28T13:00:00.000Z"),
      msg(api.MessageType.ResolutionResponse, "2026-05-28T13:00:10.000Z"),
    ];
    expect(getHistoryQueueTimeMs(history)).toBe(10_000);
  });

  it("case 7 - clock-skewed history (end anchor before start) -> undefined", () => {
    const history: api.Message[] = [
      msg(api.MessageType.EventRequest, "2026-05-28T14:00:00.500Z"),
      // Skewed timestamps push the only end-anchor before the start anchor.
      msg(api.MessageType.DeferralResponse, "2026-05-28T13:59:59.900Z"),
    ];
    expect(getHistoryQueueTimeMs(history)).toBeUndefined();
  });

  it("normalises messageType casing so transport differences do not change the result", () => {
    // Mixed casing -> still recognised as a waited lifecycle.
    const history: api.Message[] = [
      msg("EventRequest", "2026-05-28T15:00:00.000Z"),
      msg("DEFERRALRESPONSE", "2026-05-28T15:00:05.000Z"),
    ];
    expect(getHistoryQueueTimeMs(history)).toBe(5_000);
  });

  it("ignores unknown forward-compat messageType values without throwing", () => {
    const history: api.Message[] = [
      msg("eventRequest", "2026-05-28T16:00:00.000Z"),
      msg("someFutureUnknownType" as api.MessageType, "2026-05-28T16:00:01.000Z"),
      msg("deferralResponse", "2026-05-28T16:00:02.000Z"),
    ];
    expect(getHistoryQueueTimeMs(history)).toBe(2_000);
  });
});

// ---------------------------------------------------------------------------
// FR-041 - the timing bar's Queue segment uses the overridden value on a
// deferred fixture and the row value on a non-deferred fixture (with the
// `messages` prop populated in both cases).
// ---------------------------------------------------------------------------
describe("MessageListing timing bar (spec 005)", () => {
  function eventFixture(overrides: Partial<api.IEvent>): api.Event {
    return Object.assign(new api.Event(), {
      eventId: "evt-1",
      sessionId: "sess-1",
      lastMessageId: "msg-1",
      endpointId: "ep-1",
      eventTypeId: "Demo.Type",
      from: "ep-1",
      resolutionStatus: "Completed",
      enqueuedTimeUtc: moment("2026-05-28T10:00:30.000Z"),
      updatedAt: moment("2026-05-28T10:00:30.045Z"),
      queueTimeMs: 12,
      processingTimeMs: 45,
      ...overrides,
    });
  }

  const renderListing = (
    event: api.Event,
    messages: api.Message[] | undefined,
  ) =>
    render(
      <MemoryRouter>
        <MessageListing
          eventDetails={event}
          messages={messages}
          eventTypes={[]}
          skipEvent={async () => {}}
          resubmitEvent={async () => {}}
          resubmitEventWithChanges={async () => {}}
        />
      </MemoryRouter>,
    );

  it("uses the history-derived value for a deferred lifecycle", () => {
    stubMe();
    // Row says 12 ms (the replay delay); history says the wait was 30,045 ms.
    const deferredHistory: api.Message[] = [
      msg(api.MessageType.EventRequest, "2026-05-28T10:00:00.000Z"),
      msg(api.MessageType.DeferralResponse, "2026-05-28T10:00:00.500Z"),
      msg(api.MessageType.EventRequest, "2026-05-28T10:00:30.000Z"),
      msg(api.MessageType.ResolutionResponse, "2026-05-28T10:00:30.045Z"),
    ];
    renderListing(eventFixture({}), deferredHistory);

    // 30,045 ms -> formatDurationMs renders "30.05s" (toFixed(2) rounding).
    expect(screen.getByText(/30\.05s/)).toBeTruthy();
    // FR-022: Total = Queue + Processing = 30,045 + 45 = 30,090 ms -> "30.09s".
    expect(screen.getByText(/30\.09s/)).toBeTruthy();
  });

  it("uses the row-level queueTimeMs for a non-deferred lifecycle", () => {
    stubMe();
    const linearHistory: api.Message[] = [
      msg(api.MessageType.EventRequest, "2026-05-28T10:00:30.000Z"),
      msg(api.MessageType.ResolutionResponse, "2026-05-28T10:00:30.045Z"),
    ];
    renderListing(eventFixture({}), linearHistory);

    // Row value of 12 ms is shown verbatim — no override applies.
    expect(screen.getByText(/Queue\s+12ms/)).toBeTruthy();
  });

  it("preserves a measured zero in queueTimeMs (?? not || for the fallback chain)", () => {
    stubMe();
    // No history override fires; row queueTimeMs is a genuine 0.
    renderListing(eventFixture({ queueTimeMs: 0 }), []);
    expect(screen.getByText(/Queue\s+0ms/)).toBeTruthy();
  });
});

// ---------------------------------------------------------------------------
// Helper: diffMs is the third-fallback timing source. A focused test guards
// the contract that the chain depends on.
// ---------------------------------------------------------------------------
describe("diffMs (spec 005 third-fallback)", () => {
  it("returns the millisecond delta between two moment values", () => {
    expect(
      diffMs(
        moment("2026-05-28T10:00:00.500Z"),
        moment("2026-05-28T10:00:00.000Z"),
      ),
    ).toBe(500);
  });

  it("returns undefined when either side is missing", () => {
    expect(diffMs(undefined, moment())).toBeUndefined();
    expect(diffMs(moment(), undefined)).toBeUndefined();
  });
});
