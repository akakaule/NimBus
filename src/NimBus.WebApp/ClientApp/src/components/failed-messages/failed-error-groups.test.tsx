import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import moment from "moment";
import * as api from "api-client";
import { ToastProvider } from "components/ui";
import FailedErrorGroupsView from "./failed-error-groups";

const ref = (eventId: string, endpointId = "Crm") =>
  new api.FailedEventRef({
    eventId,
    lastMessageId: `m-${eventId}`,
    endpointId,
    sessionId: `s-${eventId}`,
    eventTypeId: "OrderPlaced",
    resolutionStatus: "Failed",
    updatedAt: moment("2026-09-25T10:00:00Z"),
    errorText: `HttpRequestException: 503 for ${eventId}`,
  });

const sub = (pattern: string, events: api.FailedEventRef[]) =>
  new api.FailedErrorSubGroup({
    normalizedPattern: pattern,
    count: events.length,
    endpoints: ["Crm"],
    eventTypes: ["OrderPlaced"],
    latestOccurrence: moment("2026-09-25T10:00:00Z"),
    exampleErrorText: pattern,
    events,
  });

const groups = new api.FailedErrorGroups({
  total: 4,
  truncated: false,
  groups: [
    new api.FailedErrorGroup({
      errorCategory: "HttpRequestException",
      count: 3,
      endpoints: ["Crm"],
      eventTypes: ["OrderPlaced"],
      latestOccurrence: moment("2026-09-25T10:00:00Z"),
      exampleErrorText: "HttpRequestException: 503",
      subGroups: [
        sub("HttpRequestException: 503 for <value>", [
          ref("aaaa1111"),
          ref("bbbb2222"),
        ]),
        sub("HttpRequestException: timeout", [ref("cccc3333")]),
      ],
    }),
    new api.FailedErrorGroup({
      errorCategory: "Unsupported",
      count: 1,
      endpoints: ["Erp"],
      eventTypes: ["OrderPlaced"],
      latestOccurrence: moment("2026-09-25T09:00:00Z"),
      exampleErrorText: "Unsupported: no handler for this event type",
      subGroups: [
        sub("Unsupported: no handler for this event type", [
          ref("dddd4444", "Erp"),
        ]),
      ],
    }),
  ],
});

const renderView = (onAct = vi.fn()) => {
  render(
    <ToastProvider>
      <MemoryRouter>
        <FailedErrorGroupsView
          groups={groups}
          isLoading={false}
          onAct={onAct}
        />
      </MemoryRouter>
    </ToastProvider>,
  );
  return onAct;
};

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("FailedErrorGroupsView", () => {
  it("expands a category into its patterns, and a pattern into its failures", () => {
    renderView();
    expect(screen.queryByText("HttpRequestException: timeout")).toBeNull();

    fireEvent.click(screen.getByText("HttpRequestException"));
    expect(screen.getByText("2 patterns")).toBeTruthy();
    // The pattern cell comes before its identical example-error cell.
    const pattern = screen.getAllByText("HttpRequestException: timeout")[0];
    expect(screen.queryByText("cccc3333…")).toBeNull();

    fireEvent.click(pattern);
    const link = screen.getByText("cccc3333…").closest("a");
    expect(link?.getAttribute("href")).toBe("/Message/Index/Crm/cccc3333/0");
  });

  it("expands a single-pattern category straight to its failures", () => {
    renderView();

    fireEvent.click(screen.getByText("Unsupported"));

    expect(screen.getByText("dddd4444…")).toBeTruthy();
  });

  it("resubmits every failure of a category after confirming", () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    const onAct = renderView();

    fireEvent.click(screen.getByRole("button", { name: "Resubmit 3" }));

    expect(onAct).toHaveBeenCalledTimes(1);
    const [action, events] = onAct.mock.calls[0];
    expect(action).toBe("Resubmit");
    expect(events.map((e: api.FailedEventRef) => e.eventId)).toEqual([
      "aaaa1111",
      "bbbb2222",
      "cccc3333",
    ]);
  });

  it("does nothing when the bulk action is not confirmed", () => {
    vi.spyOn(window, "confirm").mockReturnValue(false);
    const onAct = renderView();

    fireEvent.click(screen.getByRole("button", { name: "Resubmit 3" }));

    expect(onAct).not.toHaveBeenCalled();
  });
});
