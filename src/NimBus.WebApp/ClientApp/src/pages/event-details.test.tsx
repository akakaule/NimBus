import { describe, it, expect, afterEach, vi } from "vitest";
import { cleanup, render, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import moment from "moment";
import * as api from "api-client";

// Spec 005 (FR-042): histories must flow from EventDetails state into the
// MessageListing `messages` prop. The captured-props mock asserts the
// wiring directly without needing to render the full MessageListing tree.
const capturedProps: { latest?: Record<string, unknown> } = {};
vi.mock("components/event-details/message-listing", () => ({
  default: (props: Record<string, unknown>) => {
    capturedProps.latest = props;
    return <div data-testid="message-listing-stub" />;
  },
}));

// FlowTimeline / BlockedListing / Loading / TabSelection / Page are rendered
// by the page but we only need to inspect the MessageListing wiring. Stub
// the children that pull in heavy deps so the test stays fast and hermetic.
vi.mock("components/event-details/flow-timeline", () => ({
  default: () => <div data-testid="flow-timeline-stub" />,
}));
vi.mock("components/event-details/blocked-listing", () => ({
  default: () => <div data-testid="blocked-stub" />,
}));
vi.mock("components/loading/loading", () => ({
  default: () => <div data-testid="loading-stub" />,
}));

// Mock the api-client so EventDetails resolves data deterministically. Only
// the calls EventDetails actually makes need to be stubbed; everything else
// can stay as the real export.
const sampleHistory: api.Message[] = [
  Object.assign(new api.Message(), {
    messageType: api.MessageType.EventRequest,
    enqueuedTimeUtc: moment("2026-05-28T09:59:30.000Z"),
  }),
  Object.assign(new api.Message(), {
    messageType: api.MessageType.DeferralResponse,
    enqueuedTimeUtc: moment("2026-05-28T10:00:00.000Z"),
  }),
];

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") = await vi.importActual("api-client");
  class FakeClient {
    getEventId = vi.fn().mockResolvedValue(
      Object.assign(new actual.Event(), {
        eventId: "evt-1",
        sessionId: "sess-1",
        endpointId: "ep-1",
        lastMessageId: "msg-1",
        resolutionStatus: "Completed",
      }),
    );
    getEventDetailsHistoryId = vi.fn().mockResolvedValue(sampleHistory);
    getMessageAuditsEventId = vi.fn().mockResolvedValue([]);
    getEventTypes = vi.fn().mockResolvedValue([]);
    getEventBlockedId = vi.fn().mockResolvedValue({ items: [], total: 0 });
    getEventIds = vi.fn();
  }
  return {
    ...actual,
    Client: FakeClient,
    CookieAuth: () => ({}),
  };
});

afterEach(() => {
  cleanup();
  capturedProps.latest = undefined;
});

describe("EventDetails -> MessageListing wiring (spec 005)", () => {
  it("forwards the fetched history into the MessageListing messages prop", async () => {
    // Import after mocks are wired so EventDetails picks up the stubbed
    // api-client and MessageListing.
    const { default: EventDetails } = await import("./event-details");

    render(
      <MemoryRouter initialEntries={["/Message/Index/ep-1/evt-1/0"]}>
        <Routes>
          <Route
            path="/Message/Index/:endpointId/:id/:backindex"
            element={<EventDetails />}
          />
        </Routes>
      </MemoryRouter>,
    );

    // Wait until the page's effect has resolved and MessageListing has
    // received its props (the captured-props mock fires on every render).
    await waitFor(() => {
      expect(capturedProps.latest).toBeDefined();
      expect(
        (capturedProps.latest as { messages?: api.Message[] }).messages,
      ).toEqual(sampleHistory);
    });
  });
});
