import { describe, it, expect, afterEach, beforeEach, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import AuditTab from "./audit-tab";

const mocks = vi.hoisted(() => ({
  postAuditsSearch: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") =
    await vi.importActual("api-client");
  class FakeClient {
    postAuditsSearch = mocks.postAuditsSearch;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

beforeEach(() => {
  mocks.postAuditsSearch.mockReset().mockResolvedValue({
    audits: [
      {
        auditType: "searchEvents",
        auditorName: "Local Developer",
        endpointId: "crm",
        createdAt: new Date("2026-09-29T10:36:49Z"),
      },
      {
        auditType: "FailureClassified",
        auditorName: "Local Developer",
        endpointId: "crm",
        createdAt: new Date("2026-09-29T09:59:58Z"),
      },
    ],
  });
});

afterEach(() => {
  cleanup();
});

describe("AuditTab", () => {
  // The endpoint tab and the Audit Log page describe the same rows, so the
  // Action column reads the same on both — never the raw wire spelling.
  it("shows actions in the Audit Log page's wording", async () => {
    render(
      <MemoryRouter>
        <AuditTab endpointId="crm" />
      </MemoryRouter>,
    );

    expect(await screen.findByText("Search events")).toBeTruthy();
    expect(screen.getByText("Failure classified")).toBeTruthy();
    expect(screen.queryByText("searchEvents")).toBeNull();
  });
});
