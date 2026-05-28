import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import * as api from "api-client";
import MessageListing from "./message-listing";

// Spec 006 — component test for the "Blocked by" PropertyRow render-conditionality.
// Verifies all three branches of FR-020:
//  1. deferred + valid blockedByEventId + endpointId  → row renders with link
//  2. deferred + no blockedByEventId                 → row hidden
//  3. non-deferred + valid blockedByEventId          → row hidden

// useToast lives in a provider we don't bring into the test — stub it.
vi.mock("components/ui/toast", () => ({
  useToast: () => ({ addToast: vi.fn() }),
}));

// MessageListing fires getMe() on mount; resolve with no name so the component
// keeps its "operator" fallback. Avoids real network calls under jsdom. The
// Client mock must be a real class (constructable with `new`) — vi.fn()'s
// implementation isn't a constructor.
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

const ENDPOINT_ID = "endpoint-a";
const BLOCKED_BY = "cce3b12a-1234-5678-9abc-def012345678";

function buildEvent(overrides: Partial<api.Event>): api.Event {
  // The component only reads a handful of fields off the event for the
  // identifier section we're testing; cast through `any` to avoid having to
  // construct the full nswag model.
  return {
    eventId: "11111111-1111-1111-1111-111111111111",
    sessionId: "session-1",
    lastMessageId: "22222222-2222-2222-2222-222222222222",
    endpointId: ENDPOINT_ID,
    ...overrides,
  } as unknown as api.Event;
}

function renderListing(eventDetails: api.Event, blockedByEventId?: string) {
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

  afterEach(() => {
    cleanup();
  });

  it("renders a 'Blocked by' link for a deferred event with a valid blockedByEventId", () => {
    renderListing(
      buildEvent({ resolutionStatus: "Deferred" }),
      BLOCKED_BY,
    );

    expect(screen.getByText("Blocked by")).toBeTruthy();
    const link = screen.getByRole("link", { name: BLOCKED_BY });
    expect(link.getAttribute("href")).toBe(
      `/Message/Index/${ENDPOINT_ID}/${BLOCKED_BY}/0`,
    );
  });

  it("does not render 'Blocked by' for a deferred event when blockedByEventId is omitted", () => {
    renderListing(buildEvent({ resolutionStatus: "Deferred" }), undefined);
    expect(screen.queryByText("Blocked by")).toBeNull();
  });

  it("does not render 'Blocked by' for a non-deferred event even when blockedByEventId is provided", () => {
    renderListing(buildEvent({ resolutionStatus: "Completed" }), BLOCKED_BY);
    expect(screen.queryByText("Blocked by")).toBeNull();
  });
});
