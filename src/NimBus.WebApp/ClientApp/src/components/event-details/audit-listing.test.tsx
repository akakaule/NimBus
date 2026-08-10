import { describe, it, expect, afterEach, beforeEach, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import moment from "moment";
import * as api from "api-client";
import AuditListing from "./audit-listing";

// Toasts only fire from the copy-to-clipboard handler; stub the module so the
// component doesn't need a ToastProvider mounted.
vi.mock("functions/notifications.functions", () => ({
  notifySuccess: vi.fn(),
  notifyError: vi.fn(),
  notifyWarning: vi.fn(),
  notifyInfo: vi.fn(),
}));

const clientMocks = vi.hoisted(() => ({
  postAuditsSearch: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") = await vi.importActual("api-client");
  class FakeClient {
    postAuditsSearch = clientMocks.postAuditsSearch;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

function auditEntry(overrides: Partial<api.AuditEntry>): api.AuditEntry {
  return Object.assign(new api.AuditEntry(), {
    eventId: "evt-1",
    endpointId: "BillingEndpoint",
    auditorName: "Alvin Kaule",
    auditTimestamp: moment("2026-08-09T18:03:31.000Z"),
    auditType: api.AuditEntryAuditType.GetEventDetails,
    accessDenied: false,
    ...overrides,
  });
}

function response(audits: api.AuditEntry[], continuationToken?: string) {
  return Object.assign(new api.AuditSearchResponse(), {
    audits,
    continuationToken,
  });
}

function renderListing() {
  // DataTable calls useNavigate for row routing, so a Router must be present.
  return render(
    <MemoryRouter>
      <AuditListing endpointId="BillingEndpoint" eventId="evt-1" />
    </MemoryRouter>,
  );
}

describe("AuditListing", () => {
  beforeEach(() => {
    clientMocks.postAuditsSearch.mockResolvedValue(
      response([auditEntry({ comment: "looked at it" })]),
    );
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it("scopes the search to both the event and the endpoint", async () => {
    renderListing();

    await waitFor(() => {
      expect(clientMocks.postAuditsSearch).toHaveBeenCalled();
    });
    const request = clientMocks.postAuditsSearch.mock.calls[0][0];
    expect(request.filter.eventId).toBe("evt-1");
    // Not merely a filter: /api/audits/search demands site Owner when no
    // endpoint is supplied, so omitting it would lock the tab to admins.
    expect(request.filter.endpointId).toBe("BillingEndpoint");
    expect(request.maxItemCount).toBe(30);
  });

  it("renders the audit row including data and access denied", async () => {
    clientMocks.postAuditsSearch.mockResolvedValue(
      response([
        auditEntry({
          auditType: api.AuditEntryAuditType.Resubmit,
          comment: "retried after fix",
          data: '{"eventTypeId":"OrderPlaced"}',
          accessDenied: true,
        }),
      ]),
    );

    renderListing();

    const table = await screen.findByRole("table");
    const scoped = within(table);
    expect(scoped.getByText("Alvin Kaule")).toBeDefined();
    expect(scoped.getByText("retried after fix")).toBeDefined();
    expect(scoped.getByText('{"eventTypeId":"OrderPlaced"}')).toBeDefined();
    // A denied row reads as a badge rather than the word "false" in a column
    // of "false"s.
    expect(scoped.getByText("Denied")).toBeDefined();
  });

  it("offers Fetch All only when the server reports more pages", async () => {
    renderListing();

    await screen.findByRole("table");
    expect(screen.queryByRole("button", { name: /Fetch All/ })).toBeNull();
  });

  it("pages through every continuation token when Fetch All is clicked", async () => {
    clientMocks.postAuditsSearch.mockResolvedValueOnce(
      response([auditEntry({ comment: "first" })], "token-1"),
    );

    renderListing();

    const fetchAll = await screen.findByRole("button", { name: /Fetch All/ });

    // Two more pages, the last with no continuation token.
    clientMocks.postAuditsSearch
      .mockResolvedValueOnce(response([auditEntry({ comment: "page-a" })], "token-2"))
      .mockResolvedValueOnce(response([auditEntry({ comment: "page-b" })], undefined));

    fireEvent.click(fetchAll);

    await waitFor(() => {
      expect(screen.queryByRole("button", { name: /Fetch All/ })).toBeNull();
    });

    const table = screen.getByRole("table");
    const scoped = within(table);
    expect(scoped.getByText("page-a")).toBeDefined();
    expect(scoped.getByText("page-b")).toBeDefined();

    // Full page size once the operator opts into everything.
    const lastRequest = clientMocks.postAuditsSearch.mock.calls.at(-1)![0];
    expect(lastRequest.maxItemCount).toBe(200);
    expect(lastRequest.continuationToken).toBe("token-2");
  });

  it("reports a load failure instead of showing an empty trail", async () => {
    clientMocks.postAuditsSearch.mockRejectedValue(new Error("boom"));
    const consoleError = vi.spyOn(console, "error").mockImplementation(() => {});

    try {
      renderListing();

      expect(
        await screen.findByText("Could not load the audit trail for this event."),
      ).toBeDefined();
    } finally {
      consoleError.mockRestore();
    }
  });
});
