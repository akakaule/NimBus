import { describe, it, expect, afterEach, vi } from "vitest";
import { cleanup, render, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import * as api from "api-client";
import { ToastProvider } from "components/ui/toast";

// Stub heavy layout components that pull in deps not needed for this test.
vi.mock("components/loading/loading", () => ({
  default: () => <div data-testid="loading-stub" />,
}));

vi.mock("components/page", () => ({
  default: ({ children }: { children: React.ReactNode }) => (
    <div data-testid="page-stub">{children}</div>
  ),
}));

// One Draft mapping with transform, rationale and worked examples.
const sampleMapping = Object.assign(new api.MappingInfo(), {
  id: "map-1",
  sourceEventTypeId: "SourceEvent",
  targetEventTypeId: "TargetEvent",
  transform: `function transform(src) { return { id: src.externalId }; }`,
  rationale: "Maps the external CRM event to the internal domain model.",
  workedExamplesJson: JSON.stringify([
    {
      source: { externalId: "EXT-001", name: "Test" },
      output: { id: "EXT-001" },
    },
  ]),
  state: api.MappingInfoState.Draft,
  version: 1,
});

const mockApprove = vi.fn().mockResolvedValue(undefined);
const mockReject = vi.fn().mockResolvedValue(undefined);
const mockPause = vi.fn().mockResolvedValue(undefined);
const mockResume = vi.fn().mockResolvedValue(undefined);
const mockGetMappings = vi.fn().mockResolvedValue([sampleMapping]);

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") = await vi.importActual("api-client");
  class FakeClient {
    getAgentMappings = mockGetMappings;
    postAgentMappingApprove = mockApprove;
    postAgentMappingReject = mockReject;
    postAgentMappingPause = mockPause;
    postAgentMappingResume = mockResume;
  }
  return {
    ...actual,
    Client: FakeClient,
    CookieAuth: () => ({}),
  };
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
  // Reset to the default resolved value after clearAllMocks.
  mockGetMappings.mockResolvedValue([sampleMapping]);
});

describe("Mappings page (spec 023)", () => {
  it("renders the transform text for a Draft mapping", async () => {
    const { default: MappingsPage } = await import("./mappings");

    const { getByText } = render(
      <ToastProvider>
        <MemoryRouter>
          <MappingsPage />
        </MemoryRouter>
      </ToastProvider>,
    );

    // The mapping row should appear.
    await waitFor(() => {
      expect(getByText("SourceEvent")).toBeTruthy();
    });

    // Click the row to select it and show the detail panel.
    const row = getByText("SourceEvent").closest("[data-testid='mapping-row']") ??
      getByText("SourceEvent");
    await userEvent.click(row);

    // Transform and rationale should render in the detail panel.
    await waitFor(() => {
      expect(getByText(/function transform/)).toBeTruthy();
      expect(getByText(/Maps the external CRM event/)).toBeTruthy();
    });
  });

  it("renders a worked example source value", async () => {
    const { default: MappingsPage } = await import("./mappings");

    const { getByText, getAllByText } = render(
      <ToastProvider>
        <MemoryRouter>
          <MappingsPage />
        </MemoryRouter>
      </ToastProvider>,
    );

    await waitFor(() => {
      expect(getByText("SourceEvent")).toBeTruthy();
    });

    const row = getByText("SourceEvent").closest("[data-testid='mapping-row']") ??
      getByText("SourceEvent");
    await userEvent.click(row);

    // "EXT-001" appears inside the worked example source/output JSON (may be multiple spans).
    await waitFor(() => {
      expect(getAllByText(/EXT-001/).length).toBeGreaterThan(0);
    });
  });

  it("calls postAgentMappingApprove with the mapping id when Approve is clicked", async () => {
    const { default: MappingsPage } = await import("./mappings");

    const { getByText, getByRole } = render(
      <ToastProvider>
        <MemoryRouter>
          <MappingsPage />
        </MemoryRouter>
      </ToastProvider>,
    );

    await waitFor(() => {
      expect(getByText("SourceEvent")).toBeTruthy();
    });

    const row = getByText("SourceEvent").closest("[data-testid='mapping-row']") ??
      getByText("SourceEvent");
    await userEvent.click(row);

    await waitFor(() => {
      expect(getByRole("button", { name: /approve/i })).toBeTruthy();
    });

    await userEvent.click(getByRole("button", { name: /approve/i }));

    await waitFor(() => {
      expect(mockApprove).toHaveBeenCalledWith("map-1");
    });
  });

  it("keeps the same mapping selected (with updated state) after an action refreshes the list", async () => {
    const { default: MappingsPage } = await import("./mappings");

    // After Approve, the refresh returns the SAME mapping id but now Active.
    const approvedMapping = Object.assign(new api.MappingInfo(), {
      ...sampleMapping,
      state: api.MappingInfoState.Active,
    });
    mockGetMappings
      .mockResolvedValueOnce([sampleMapping])
      .mockResolvedValueOnce([approvedMapping]);

    const { getByText, getByRole, queryByRole } = render(
      <ToastProvider>
        <MemoryRouter>
          <MappingsPage />
        </MemoryRouter>
      </ToastProvider>,
    );

    await waitFor(() => {
      expect(getByText("SourceEvent")).toBeTruthy();
    });

    const row = getByText("SourceEvent").closest("[data-testid='mapping-row']") ??
      getByText("SourceEvent");
    await userEvent.click(row);

    await waitFor(() => {
      expect(getByRole("button", { name: /approve/i })).toBeTruthy();
    });

    await userEvent.click(getByRole("button", { name: /approve/i }));

    // Selection persists across the post-action refresh: the detail panel now
    // reflects the Active state (Pause button shown, Approve gone).
    await waitFor(() => {
      expect(getByRole("button", { name: /pause/i })).toBeTruthy();
      expect(queryByRole("button", { name: /approve/i })).toBeNull();
    });
  });
});

describe("describeActionError", () => {
  it("shows schema-drift guidance for a 409 SwaggerException", async () => {
    const { describeActionError } = await import("./mappings");
    const err = new api.SwaggerException("Conflict", 409, "", {}, null);
    expect(describeActionError(err)).toBe(
      "Mapping has drifted since proposal — reject and re-propose.",
    );
  });

  it("shows the numeric status for a non-409 SwaggerException", async () => {
    const { describeActionError } = await import("./mappings");
    const err = new api.SwaggerException("Server error", 500, "", {}, null);
    expect(describeActionError(err)).toBe("Action failed (500).");
  });

  it("falls back to the message for a plain Error", async () => {
    const { describeActionError } = await import("./mappings");
    expect(describeActionError(new Error("boom"))).toBe(
      "Action failed (boom).",
    );
  });

  it("handles a non-Error thrown value", async () => {
    const { describeActionError } = await import("./mappings");
    expect(describeActionError("nope")).toBe("Action failed (unknown).");
  });
});
