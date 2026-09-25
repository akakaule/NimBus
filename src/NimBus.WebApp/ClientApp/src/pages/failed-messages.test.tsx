import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
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

// The chart is recharts (no layout in jsdom); expose its selection callback as a button.
vi.mock("components/failed-messages/failed-histogram", () => ({
  default: (props: { onSelectBucket: (start: string) => void }) => (
    <button
      type="button"
      onClick={() => props.onSelectBucket("2026-09-25T06:00:00.000Z")}
    >
      select-bar
    </button>
  ),
}));

vi.mock("components/failed-messages/failed-filter-bar", () => ({
  default: () => <div data-testid="filter-bar-stub" />,
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

    await waitFor(() => expect(captured.rows?.length).toBe(3));
    const histogramRequest = mocks.histogram.mock
      .calls[0][0] as api.FailedHistogramRequest;
    expect(histogramRequest.period).toBe(api.Period._7d);
    expect(histogramRequest.filter?.updatedAtFrom).toBeUndefined();

    const searchRequest = mocks.search.mock
      .calls[0][0] as api.FailedSearchRequest;
    expect(searchRequest.filter?.updatedAtFrom).toBeDefined();
    expect(captured.rows?.[0].route).toBe("/Message/Index/Crm/e1/0");
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
      "2026-09-25T11:59:59.999Z",
    );
    expect(mocks.histogram).toHaveBeenCalledTimes(1);
    expect(screen.getByLabelText("Clear time window")).toBeTruthy();
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

  it("resubmits a row and removes it from the list", async () => {
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
    await renderPage();
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
