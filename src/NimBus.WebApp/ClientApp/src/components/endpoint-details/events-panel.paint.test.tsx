import { describe, it, expect, afterEach, vi } from "vitest";
import { act, render, screen, cleanup, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import * as api from "api-client";

// Shared mock handles. `getByFilterMock` resolves the events immediately;
// `postSessionsBatchMock` returns a promise we resolve by hand so the test can
// observe the table BEFORE the session-status batch settles.
const { sessionsDeferreds, postSessionsBatchMock, getByFilterMock } = vi.hoisted(
  () => {
    const sessionsDeferreds: Array<(value: unknown) => void> = [];
    const postSessionsBatchMock = vi.fn(
      () =>
        new Promise((resolve) => {
          sessionsDeferreds.push(resolve);
        }),
    );
    const getByFilterMock = vi.fn();
    return { sessionsDeferreds, postSessionsBatchMock, getByFilterMock };
  },
);

// DataTable reads the toast provider for action feedback; no-op it.
vi.mock("components/ui/toast", () => ({
  useToast: () => ({ addToast: () => {} }),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") =
    await vi.importActual("api-client");
  class FakeClient {
    postApiEventEndpointIdGetByFilter = getByFilterMock;
    postEndpointSessionsBatch = postSessionsBatchMock;
    // EventTypeFiltering fetches the endpoint's event-type catalog on mount.
    getEventtypesByEndpointId = vi.fn().mockResolvedValue({});
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

afterEach(() => {
  cleanup();
  sessionsDeferreds.length = 0;
  postSessionsBatchMock.mockClear();
  getByFilterMock.mockReset();
});

describe("EventsPanel paints before the session-status batch resolves", () => {
  it("renders event rows while postEndpointSessionsBatch is still pending, then hydrates the counts", async () => {
    const failedEvent = Object.assign(new api.Event(), {
      eventId: "evt-1",
      sessionId: "sess-1",
      eventTypeId: "MyUniqueEventType",
      lastMessageId: "msg-1",
      resolutionStatus: api.ResolutionStatus.Failed,
    });
    getByFilterMock.mockResolvedValue({
      events: [failedEvent],
      continuationToken: undefined,
    });

    const { default: EventsPanel } = await import("./events-panel");
    render(
      <MemoryRouter>
        <EventsPanel endpointId="ep-1" />
      </MemoryRouter>,
    );

    // The row must paint even though the session-status batch has NOT resolved
    // yet — the batch only fills the count columns, so the table should not wait
    // on it. (With the old awaited batch, isLoading stays true and DataTable
    // renders only a spinner, so this text never appears.)
    await waitFor(() =>
      expect(screen.getByText("MyUniqueEventType")).toBeTruthy(),
    );
    expect(postSessionsBatchMock).toHaveBeenCalledTimes(1);
    expect(sessionsDeferreds.length).toBe(1); // batch is still in flight

    // Resolving the batch hydrates the per-session Pending count into the row.
    await act(async () => {
      sessionsDeferreds[0]([
        { deferredEvents: [], pendingEvents: ["a_sess-1", "b_sess-1", "c_sess-1"] },
      ]);
    });

    await waitFor(() => expect(screen.getByText("3")).toBeTruthy());
  });
});
