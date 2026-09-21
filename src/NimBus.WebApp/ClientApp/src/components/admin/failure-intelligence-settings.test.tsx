import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import FailureIntelligenceSettings from "./failure-intelligence-settings";

const originalFetch = globalThis.fetch;
const settings = { enabled: true, model: "jev-1.13.0", includeEventPayload: false, includeRecentFailureHistory: true,
  maximumHistoryItems: 5, additionalRedactedKeys: [], allowedEndpoints: [], timeoutSeconds: 20,
  minimumCategoryConfidence: 0.6, retryLikely: 0.75, changeRequired: 0.75 };
const state = { active: settings, saved: settings, revision: "none", activeRevision: "none", restartRequired: false,
  startupLoadFailed: false, credentialConfigured: true, csrfToken: "csrf-test" };
afterEach(() => { cleanup(); globalThis.fetch = originalFetch; });

describe("Failure intelligence settings", () => {
  it("reviews payload sharing and saves with CSRF and a revision without changing active settings", async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(Response.json(state)).mockResolvedValueOnce(Response.json({
      ...state, saved: { ...settings, includeEventPayload: true }, revision: "new-revision", restartRequired: true,
    }));
    globalThis.fetch = fetchMock as typeof fetch;
    render(<FailureIntelligenceSettings />);
    await userEvent.click(await screen.findByRole("switch", { name: "Include redacted event payload" }));
    expect(screen.getByText(/Event data will leave NimBus/)).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Review changes" }));
    expect((screen.getByRole("button", { name: "Save settings" }) as HTMLButtonElement).disabled).toBe(true);
    await userEvent.click(screen.getByRole("checkbox", { name: /I authorize/ }));
    await userEvent.click(screen.getByRole("button", { name: "Save settings" }));
    await screen.findByText(/Settings saved. Restart all WebApp instances/);
    const request = fetchMock.mock.calls[1][1];
    expect(request.headers["X-NimBus-CSRF"]).toBe("csrf-test");
    expect(JSON.parse(request.body)).toMatchObject({ revision: "none", payloadSharingAcknowledged: true, settings: { includeEventPayload: true } });
    expect(screen.getByText(/Active on this instance: enabled · payload excluded/)).toBeTruthy();
  });

  it("shows forbidden without rendering editable controls", async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(new Response(null, { status: 403 })) as typeof fetch;
    render(<FailureIntelligenceSettings />);
    await screen.findByText(/Only site Owners/);
    expect(screen.queryByRole("switch")).toBeNull();
  });

  it("preserves edits and explains concurrent-save conflicts", async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(Response.json(state)).mockResolvedValueOnce(new Response(null, { status: 409 }));
    globalThis.fetch = fetchMock as typeof fetch;
    render(<FailureIntelligenceSettings />);
    const model = await screen.findByLabelText("Model");
    await userEvent.clear(model);
    await userEvent.type(model, "jev-test");
    await userEvent.click(screen.getByRole("button", { name: "Review changes" }));
    await userEvent.click(screen.getByRole("button", { name: "Save settings" }));
    await screen.findByText(/Another administrator/);
    expect((model as HTMLInputElement).value).toBe("jev-test");
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
  });
});
