import { describe, it, expect, afterEach, beforeEach, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import moment from "moment";
import * as api from "api-client";
import MessageListing, {
  diffMs,
  getHistoryQueueTimeMs,
} from "./message-listing";

// MessageListing internally constructs an api.Client and queries `getMe()` on
// mount to label handoff actions. Stub the toast provider and the api Client
// so the component tests stay hermetic.
vi.mock("components/ui/toast", () => ({
  useToast: () => ({ addToast: () => {} }),
}));

vi.mock("api-client", async () => {
  const actual = await vi.importActual<typeof import("api-client")>("api-client");
  class MockClient {
    getMe() {
      return Promise.resolve(undefined);
    }
    postMessageAudit() {
      return Promise.resolve(undefined);
    }
  }
  return {
    ...actual,
    Client: MockClient,
    CookieAuth: () => ({}),
  };
});

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
    expect(getHistoryQueueTimeMs(history)).toBe(30_050);
  });

  it("case 4 - pending-handoff that resolves via HandoffCompletedRequest", () => {
    const history: api.Message[] = [
      msg(api.MessageType.EventRequest, "2026-05-28T11:00:00.000Z"),
      msg(api.MessageType.PendingHandoffResponse, "2026-05-28T11:00:00.040Z"),
      msg(api.MessageType.HandoffCompletedRequest, "2026-05-28T11:05:00.000Z"),
      msg(api.MessageType.ResolutionResponse, "2026-05-28T11:05:00.080Z"),
    ];
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
      msg(api.MessageType.DeferralResponse, "2026-05-28T13:00:00.000Z"),
      msg(api.MessageType.ResolutionResponse, "2026-05-28T13:00:10.000Z"),
    ];
    expect(getHistoryQueueTimeMs(history)).toBe(10_000);
  });

  it("case 7 - clock-skewed history (end anchor before start) -> undefined", () => {
    const history: api.Message[] = [
      msg(api.MessageType.EventRequest, "2026-05-28T14:00:00.500Z"),
      msg(api.MessageType.DeferralResponse, "2026-05-28T13:59:59.900Z"),
    ];
    expect(getHistoryQueueTimeMs(history)).toBeUndefined();
  });

  it("normalises messageType casing so transport differences do not change the result", () => {
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
// FR-041 - timing bar uses overridden value on a deferred fixture.
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
    const deferredHistory: api.Message[] = [
      msg(api.MessageType.EventRequest, "2026-05-28T10:00:00.000Z"),
      msg(api.MessageType.DeferralResponse, "2026-05-28T10:00:00.500Z"),
      msg(api.MessageType.EventRequest, "2026-05-28T10:00:30.000Z"),
      msg(api.MessageType.ResolutionResponse, "2026-05-28T10:00:30.045Z"),
    ];
    renderListing(eventFixture({}), deferredHistory);

    expect(screen.getByText(/30\.05s/)).toBeTruthy();
    expect(screen.getByText(/30\.09s/)).toBeTruthy();
  });

  it("uses the row-level queueTimeMs for a non-deferred lifecycle", () => {
    stubMe();
    const linearHistory: api.Message[] = [
      msg(api.MessageType.EventRequest, "2026-05-28T10:00:30.000Z"),
      msg(api.MessageType.ResolutionResponse, "2026-05-28T10:00:30.045Z"),
    ];
    renderListing(eventFixture({}), linearHistory);

    expect(screen.getByText(/Queue\s+12ms/)).toBeTruthy();
  });

  it("preserves a measured zero in queueTimeMs (?? not || for the fallback chain)", () => {
    stubMe();
    renderListing(eventFixture({ queueTimeMs: 0 }), []);
    expect(screen.getByText(/Queue\s+0ms/)).toBeTruthy();
  });
});

// ---------------------------------------------------------------------------
// Helper: diffMs is the third-fallback timing source.
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

// ---------------------------------------------------------------------------
// Spec 006 — render-conditionality for the "Blocked by" PropertyRow.
// ---------------------------------------------------------------------------
const SPEC_006_ENDPOINT_ID = "endpoint-a";
const SPEC_006_BLOCKED_BY = "cce3b12a-1234-5678-9abc-def012345678";

function spec006BuildEvent(overrides: Partial<api.Event>): api.Event {
  return {
    eventId: "11111111-1111-1111-1111-111111111111",
    sessionId: "session-1",
    lastMessageId: "22222222-2222-2222-2222-222222222222",
    endpointId: SPEC_006_ENDPOINT_ID,
    ...overrides,
  } as unknown as api.Event;
}

function spec006RenderListing(eventDetails: api.Event, blockedByEventId?: string) {
  return render(
    <MemoryRouter>
      <MessageListing
        eventDetails={eventDetails}
        eventTypes={[]}
        skipEvent={vi.fn()}
        resubmitEvent={vi.fn()}
        resubmitEventWithChanges={vi.fn()}
        blockedByEventId={blockedByEventId}
      />
    </MemoryRouter>,
  );
}

describe("MessageListing — Blocked by row (spec 006)", () => {
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

  it("renders a 'Blocked by' link for a deferred event with a valid blockedByEventId", () => {
    spec006RenderListing(
      spec006BuildEvent({ resolutionStatus: "Deferred" }),
      SPEC_006_BLOCKED_BY,
    );

    expect(screen.getByText("Blocked by")).toBeTruthy();
    const link = screen.getByRole("link", { name: SPEC_006_BLOCKED_BY });
    expect(link.getAttribute("href")).toBe(
      `/Message/Index/${SPEC_006_ENDPOINT_ID}/${SPEC_006_BLOCKED_BY}/0`,
    );
  });

  it("does not render 'Blocked by' for a deferred event when blockedByEventId is omitted", () => {
    spec006RenderListing(spec006BuildEvent({ resolutionStatus: "Deferred" }), undefined);
    expect(screen.queryByText("Blocked by")).toBeNull();
  });

  it("does not render 'Blocked by' for a non-deferred event even when blockedByEventId is provided", () => {
    spec006RenderListing(spec006BuildEvent({ resolutionStatus: "Completed" }), SPEC_006_BLOCKED_BY);
    expect(screen.queryByText("Blocked by")).toBeNull();
  });
});
