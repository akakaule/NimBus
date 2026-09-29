import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import moment from "moment";
import * as api from "api-client";
import type { ITableRow } from "components/data-table";
import { ToastProvider } from "components/ui";

const mocks = vi.hoisted(() => ({
  histogram: vi.fn(),
  search: vi.fn(),
  errorGroups: vi.fn(),
  statusCounts: vi.fn(),
  sessions: vi.fn(),
  resubmit: vi.fn(),
  skip: vi.fn(),
}));

const captured: { rows?: ITableRow[] } = {};
vi.mock("components/data-table", async () => {
  const actual = await vi.importActual<typeof import("components/data-table")>(
    "components/data-table",
  );
  return {
    ...actual,
    default: (props: { rows: ITableRow[] }) => {
      captured.rows = props.rows;
      return <div data-testid="data-table-stub" />;
    },
  };
});

// The chart is recharts (no layout in jsdom); expose its window callback as buttons: a
// clicked bar is one 1d bucket, a brushed range spans several.
vi.mock("components/failed-messages/failed-histogram", () => ({
  default: (props: {
    onWindowChange: (w: { from: Date; to: Date } | undefined) => void;
  }) => (
    <>
      <button
        type="button"
        onClick={() =>
          props.onWindowChange({
            from: new Date("2026-09-25T06:00:00.000Z"),
            to: new Date("2026-09-25T07:00:00.000Z"),
          })
        }
      >
        select-bar
      </button>
      <button
        type="button"
        onClick={() =>
          props.onWindowChange({
            from: new Date("2026-09-25T02:00:00.000Z"),
            to: new Date("2026-09-25T09:00:00.000Z"),
          })
        }
      >
        brush-range
      </button>
    </>
  ),
}));

// The panel's own loading is covered by its tests; here it only shows what it was opened on.
vi.mock("components/failed-messages/failure-detail-panel", () => ({
  default: (props: {
    endpointId: string;
    eventId: string;
    position?: { index: number; total: number };
    onNext?: () => void;
    onPrevious?: () => void;
    onClose: () => void;
    onAct: (action: "Resubmit" | "Skip", e: api.Event) => void;
  }) => (
    <div data-testid="panel">
      <span>
        panel {props.endpointId}/{props.eventId}{" "}
        {props.position
          ? `${props.position.index} of ${props.position.total}`
          : "not listed"}
      </span>
      <button type="button" disabled={!props.onNext} onClick={props.onNext}>
        panel-next
      </button>
      <button
        type="button"
        disabled={!props.onPrevious}
        onClick={props.onPrevious}
      >
        panel-previous
      </button>
      <button
        type="button"
        onClick={() =>
          props.onAct(
            "Resubmit",
            new api.Event({
              eventId: props.eventId,
              endpointId: props.endpointId,
              lastMessageId: `m-${props.eventId}`,
            }),
          )
        }
      >
        panel-resubmit
      </button>
    </div>
  ),
}));

vi.mock("api-client", async () => {
  const actual =
    await vi.importActual<typeof import("api-client")>("api-client");
  class FakeClient {
    postFailedHistogram = mocks.histogram;
    postFailedSearch = mocks.search;
    postFailedErrorGroups = mocks.errorGroups;
    getEndpointStatusCountAll = mocks.statusCounts;
    postEndpointSessionsBatch = mocks.sessions;
    postResubmitEventIds = mocks.resubmit;
    postSkipEventIds = mocks.skip;
    getEndpointsAll = () => Promise.resolve(["Crm", "Erp"]);
    getEventTypes = () => Promise.resolve([]);
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

const event = (eventId: string, endpointId: string, sessionId: string) =>
  new api.Event({
    eventId,
    endpointId,
    sessionId,
    lastMessageId: `m-${eventId}`,
    eventTypeId: "OrderPlaced",
    resolutionStatus: api.ResolutionStatus.Failed,
    updatedAt: moment("2026-09-25T07:00:00Z"),
    messageContent: new api.MessageContent({
      errorContent: new api.ErrorContent({
        errorText: "HttpRequestException: 503",
      }),
    }),
  });

const histogram = new api.FailedHistogram({
  bucketMinutes: 360,
  buckets: [],
  truncated: false,
  totals: new api.FailedHistogramTotals({
    failed: 3,
    deadLettered: 1,
    unsupported: 0,
    byEndpoint: [
      new api.FailedEndpointTotals({
        endpointId: "Crm",
        failed: 2,
        deadLettered: 1,
        unsupported: 0,
      }),
      new api.FailedEndpointTotals({
        endpointId: "Erp",
        failed: 1,
        deadLettered: 0,
        unsupported: 0,
      }),
    ],
  }),
});

const listItems = () =>
  within(screen.getByRole("list", { name: "Failures" })).queryAllByRole(
    "listitem",
  );

const renderPage = async (url = "/Failed") => {
  const { default: FailedMessages } = await import("./failed-messages");
  render(
    <ToastProvider>
      <MemoryRouter initialEntries={[url]}>
        <FailedMessages />
      </MemoryRouter>
    </ToastProvider>,
  );
};

beforeEach(() => {
  sessionStorage.clear();
  mocks.histogram.mockResolvedValue(histogram);
  mocks.search.mockResolvedValue(
    new api.SearchResponse({
      events: [
        event("e1", "Crm", "s1"),
        event("e2", "Erp", "s2"),
        event("e3", "Crm", "s3"),
      ],
    }),
  );
  mocks.errorGroups.mockResolvedValue(
    new api.FailedErrorGroups({ groups: [], total: 0, truncated: false }),
  );
  mocks.statusCounts.mockResolvedValue([]);
  mocks.sessions.mockResolvedValue([]);
  mocks.resubmit.mockResolvedValue(undefined);
});

afterEach(() => {
  cleanup();
  captured.rows = undefined;
  vi.clearAllMocks();
});

describe("Failed messages page", () => {
  it("loads the chart for the range and the list within it", async () => {
    await renderPage();

    await waitFor(() => expect(listItems()).toHaveLength(3));
    const histogramRequest = mocks.histogram.mock
      .calls[0][0] as api.FailedHistogramRequest;
    expect(histogramRequest.period).toBe(api.Period._1d);
    expect(histogramRequest.filter?.updatedAtFrom).toBeUndefined();

    const searchRequest = mocks.search.mock
      .calls[0][0] as api.FailedSearchRequest;
    expect(searchRequest.filter?.updatedAtFrom).toBeDefined();
    expect(screen.queryByTestId("data-table-stub")).toBeNull();
  });

  it("shows the table when View as is Table", async () => {
    await renderPage("/Failed?display=table");

    await waitFor(() => expect(captured.rows?.length).toBe(3));
    expect(captured.rows?.[0].route).toBe("/Message/Index/Crm/e1/0");
    expect(screen.queryByRole("list", { name: "Failures" })).toBeNull();
  });

  it("resubmits an item and removes it from the list", async () => {
    mocks.search
      .mockResolvedValueOnce(
        new api.SearchResponse({
          events: [event("e1", "Crm", "s1"), event("e2", "Erp", "s2")],
        }),
      )
      .mockResolvedValue(
        new api.SearchResponse({ events: [event("e2", "Erp", "s2")] }),
      );
    await renderPage();
    await waitFor(() => expect(listItems()).toHaveLength(2));

    fireEvent.click(
      within(listItems()[0]).getByRole("button", { name: "Resubmit" }),
    );

    expect(mocks.resubmit).toHaveBeenCalledWith("e1", "m-e1");
    await waitFor(() => expect(listItems()).toHaveLength(1));
    expect(within(listItems()[0]).getByText("e2")).toBeTruthy();
  });

  it("narrows the list, but not the chart, to a selected bar", async () => {
    await renderPage();
    await waitFor(() => expect(mocks.search).toHaveBeenCalledTimes(1));

    fireEvent.click(screen.getByText("select-bar"));

    await waitFor(() => expect(mocks.search).toHaveBeenCalledTimes(2));
    const request = mocks.search.mock.calls[1][0] as api.FailedSearchRequest;
    expect(request.filter?.updatedAtFrom?.toISOString()).toBe(
      "2026-09-25T06:00:00.000Z",
    );
    expect(request.filter?.updatedAtTo?.toISOString()).toBe(
      "2026-09-25T06:59:59.999Z",
    );
    expect(mocks.histogram).toHaveBeenCalledTimes(1);
    expect(screen.getByLabelText("Clear time window")).toBeTruthy();
  });

  it("narrows the list to a brushed range, and clears it again", async () => {
    await renderPage();
    await waitFor(() => expect(mocks.search).toHaveBeenCalledTimes(1));

    fireEvent.click(screen.getByText("brush-range"));

    await waitFor(() => expect(mocks.search).toHaveBeenCalledTimes(2));
    const narrowed = mocks.search.mock.calls[1][0] as api.FailedSearchRequest;
    expect(narrowed.filter?.updatedAtFrom?.toISOString()).toBe(
      "2026-09-25T02:00:00.000Z",
    );
    expect(narrowed.filter?.updatedAtTo?.toISOString()).toBe(
      "2026-09-25T08:59:59.999Z",
    );

    fireEvent.click(screen.getByLabelText("Clear time window"));

    await waitFor(() => expect(mocks.search).toHaveBeenCalledTimes(3));
    const cleared = mocks.search.mock.calls[2][0] as api.FailedSearchRequest;
    expect(cleared.filter?.updatedAtFrom?.toISOString()).not.toBe(
      "2026-09-25T02:00:00.000Z",
    );
    expect(mocks.histogram).toHaveBeenCalledTimes(1);
  });

  it("reads a legacy bar link as the list window", async () => {
    await renderPage("/Failed?period=7d&bucket=2026-09-25T06:00:00.000Z");

    await waitFor(() => expect(mocks.search).toHaveBeenCalled());
    const request = mocks.search.mock.calls[0][0] as api.FailedSearchRequest;
    expect(request.filter?.updatedAtFrom?.toISOString()).toBe(
      "2026-09-25T06:00:00.000Z",
    );
    expect(request.filter?.updatedAtTo?.toISOString()).toBe(
      "2026-09-25T11:59:59.999Z",
    );
  });

  it("searches IDs and error text from the one search box", async () => {
    await renderPage();
    await waitFor(() => expect(mocks.search).toHaveBeenCalledTimes(1));

    const box = screen.getByLabelText("Search failures");
    fireEvent.change(box, { target: { value: "session:s1 timeout 503" } });
    fireEvent.keyDown(box, { key: "Enter" });

    await waitFor(() => expect(mocks.search).toHaveBeenCalledTimes(2));
    const request = mocks.search.mock.calls[1][0] as api.FailedSearchRequest;
    expect(request.filter?.sessionId).toBe("s1");
    expect(request.filter?.errorText).toBe("timeout 503");
    expect(request.filter?.eventId).toBeUndefined();
    const chart = mocks.histogram.mock.calls.at(
      -1,
    )![0] as api.FailedHistogramRequest;
    expect(chart.filter?.sessionId).toBe("s1");
  });

  it("filters by status from a legend tile", async () => {
    await renderPage();
    await waitFor(() => expect(mocks.search).toHaveBeenCalledTimes(1));

    const totals = screen.getByLabelText("Totals");
    fireEvent.click(
      within(totals).getByRole("button", { name: /DeadLettered/ }),
    );

    await waitFor(() => expect(mocks.search).toHaveBeenCalledTimes(2));
    const request = mocks.search.mock.calls[1][0] as api.FailedSearchRequest;
    expect(request.filter?.statuses).toEqual([
      api.Statuses.Failed,
      api.Statuses.Unsupported,
    ]);
  });

  it("asks each endpoint once for the sessions its failures block", async () => {
    await renderPage();

    await waitFor(() => expect(mocks.sessions).toHaveBeenCalledTimes(2));
    const calls = Object.fromEntries(
      mocks.sessions.mock.calls.map(([ep, ids]) => [ep, ids]),
    );
    expect(calls.Crm).toEqual(["s1", "s3"]);
    expect(calls.Erp).toEqual(["s2"]);
  });

  it("groups failures by error in the By error view", async () => {
    await renderPage("/Failed?view=error");

    await waitFor(() => expect(mocks.errorGroups).toHaveBeenCalledTimes(1));
    expect(mocks.search).not.toHaveBeenCalled();
    expect(screen.getByText("No failures to group")).toBeTruthy();
  });

  it("drills from the By endpoint view into one endpoint's failures", async () => {
    await renderPage("/Failed?view=endpoint");
    await waitFor(() => expect(screen.getByText("Crm")).toBeTruthy());

    fireEvent.click(
      screen.getAllByRole("button", { name: "Show failures" })[0],
    );

    await waitFor(() => expect(mocks.search).toHaveBeenCalled());
    const request = mocks.search.mock.calls.at(
      -1,
    )![0] as api.FailedSearchRequest;
    expect(request.filter?.endpointIds).toEqual(["Crm"]);
  });

  it("opens a failure in the details panel and closes it with Escape", async () => {
    await renderPage();
    await waitFor(() => expect(listItems()).toHaveLength(3));

    fireEvent.click(
      within(listItems()[1]).getByRole("link", { name: /- FAILED$/ }),
    );

    expect(await screen.findByText("panel Erp/e2 2 of 3")).toBeTruthy();
    expect(
      screen.getByRole("dialog", { name: "Transaction details" }),
    ).toBeTruthy();

    fireEvent.keyDown(document, { key: "Escape" });

    await waitFor(() => expect(screen.queryByTestId("panel")).toBeNull());
  });

  it("opens the panel from a deep link and steps through the list", async () => {
    await renderPage("/Failed?open=Crm/e3");

    expect(await screen.findByText("panel Crm/e3 3 of 3")).toBeTruthy();
    expect(
      (screen.getByText("panel-next") as HTMLButtonElement).disabled,
    ).toBe(true);

    fireEvent.click(screen.getByText("panel-previous"));

    expect(await screen.findByText("panel Erp/e2 2 of 3")).toBeTruthy();
  });

  it("loads the next page when Next runs past the last loaded failure", async () => {
    mocks.search
      .mockResolvedValueOnce(
        new api.SearchResponse({
          events: [event("e1", "Crm", "s1"), event("e2", "Erp", "s2")],
          continuationToken: "page-2",
        }),
      )
      .mockResolvedValueOnce(
        new api.SearchResponse({ events: [event("e3", "Crm", "s3")] }),
      );
    await renderPage("/Failed?open=Erp/e2");
    expect(await screen.findByText("panel Erp/e2 2 of 2")).toBeTruthy();

    fireEvent.click(screen.getByText("panel-next"));

    expect(await screen.findByText("panel Crm/e3 3 of 3")).toBeTruthy();
    const request = mocks.search.mock.calls[1][0] as api.FailedSearchRequest;
    expect(request.continuationToken).toBe("page-2");
  });

  it("resubmits from the panel and moves on to the next failure", async () => {
    await renderPage("/Failed?open=Crm/e1");
    expect(await screen.findByText("panel Crm/e1 1 of 3")).toBeTruthy();

    fireEvent.click(screen.getByText("panel-resubmit"));

    expect(mocks.resubmit).toHaveBeenCalledWith("e1", "m-e1");
    expect(await screen.findByText(/panel Erp\/e2/)).toBeTruthy();
  });

  it("resubmits a table row and removes it from the list", async () => {
    // After the resubmit the page reloads; the server no longer lists e1.
    mocks.search
      .mockResolvedValueOnce(
        new api.SearchResponse({
          events: [
            event("e1", "Crm", "s1"),
            event("e2", "Erp", "s2"),
            event("e3", "Crm", "s3"),
          ],
        }),
      )
      .mockResolvedValue(
        new api.SearchResponse({
          events: [event("e2", "Erp", "s2"), event("e3", "Crm", "s3")],
        }),
      );
    await renderPage("/Failed?display=table");
    await waitFor(() => expect(captured.rows?.length).toBe(3));

    const resubmit = captured.rows![0].bodyActions!.find(
      (a) => a.name === "Resubmit",
    )!;
    resubmit.onClick();

    expect(mocks.resubmit).toHaveBeenCalledWith("e1", "m-e1");
    await waitFor(() =>
      expect(captured.rows?.some((r) => r.id === "Crm/e1")).toBe(false),
    );
  });
});
