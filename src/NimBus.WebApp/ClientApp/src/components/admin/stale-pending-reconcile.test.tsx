import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ToastProvider } from "components/ui";

const { previewMock, reconcileMock } = vi.hoisted(() => ({
  previewMock: vi.fn(),
  reconcileMock: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual =
    await vi.importActual<typeof import("api-client")>("api-client");
  class Client {
    postAdminStalePendingPreview = previewMock;
    postAdminStalePendingReconcile = reconcileMock;
  }
  return { ...actual, Client, CookieAuth: () => ({}) };
});

afterEach(() => {
  cleanup();
  vi.resetAllMocks();
});

const endpoints = [{ value: "Nav09Endpoint", label: "Nav09Endpoint" }];

async function renderCard() {
  const { StalePendingReconcileCard } = await import("./stale-pending-reconcile");
  render(
    <ToastProvider>
      <StalePendingReconcileCard endpoints={endpoints} />
    </ToastProvider>,
  );
  await userEvent.click(screen.getByPlaceholderText("Select endpoint..."));
  await userEvent.click(screen.getByText("Nav09Endpoint"));
}

function previewWith(verdicts: string[]) {
  return {
    endpointId: "Nav09Endpoint",
    scanned: verdicts.length,
    candidates: verdicts.length,
    repairable: verdicts.filter((v) => v === "Repairable").length,
    truncated: false,
    rows: verdicts.map((verdict, index) => ({
      eventId: `event-${index}`,
      sessionId: "session-1",
      eventTypeId: "OrderPlaced",
      rowMessageType: "EventRequest",
      staleMessageId: `req-copy-${index}`,
      rowEnqueuedTimeUtc: new Date("2026-09-01T12:02:00Z"),
      rowUpdatedAt: new Date("2026-09-01T12:02:00Z"),
      verdict,
      detail: `why-${index}`,
      responseMessageId: `rsp-${index}`,
      responseEnqueuedTimeUtc: new Date("2026-09-01T12:01:00Z"),
    })),
  };
}

describe("StalePendingReconcileCard", () => {
  it("renders the tiles and a verdict badge per row", async () => {
    previewMock.mockResolvedValue(previewWith(["Repairable", "LaterControlMessage"]));
    await renderCard();

    await userEvent.click(screen.getByRole("button", { name: "Preview" }));

    // "Repairable" is both the stat tile's label and the row's verdict badge.
    await waitFor(() => expect(screen.getAllByText("Repairable")).toHaveLength(2));
    expect(screen.getByText("LaterControlMessage")).toBeTruthy();
    expect(screen.getByText("why-0")).toBeTruthy();
    // candidates 2 / repairable 1 / operator decision 1
    expect(screen.getByText("Candidates")).toBeTruthy();
    expect(screen.getByText("Operator decision")).toBeTruthy();
    expect(
      (screen.getByRole("button", { name: "Repair 1 rows" }) as HTMLButtonElement).disabled,
    ).toBe(false);
  }, 10_000);

  it("disables Repair when nothing is repairable", async () => {
    previewMock.mockResolvedValue(previewWith(["NoTerminal"]));
    await renderCard();

    await userEvent.click(screen.getByRole("button", { name: "Preview" }));

    expect(await screen.findByText("NoTerminal")).toBeTruthy();
    expect(
      (screen.getByRole("button", { name: "Repair 0 rows" }) as HTMLButtonElement).disabled,
    ).toBe(true);
    expect(reconcileMock).not.toHaveBeenCalled();
  }, 10_000);

  it("repairs after the endpoint id is typed, then re-previews", async () => {
    previewMock
      .mockResolvedValueOnce(previewWith(["Repairable"]))
      .mockResolvedValueOnce(previewWith([]));
    reconcileMock.mockResolvedValue({
      processed: 1,
      succeeded: 1,
      failed: 0,
      skipped: 0,
      errors: [],
      repairedEventIds: ["event-0"],
    });
    await renderCard();

    await userEvent.click(screen.getByRole("button", { name: "Preview" }));
    await userEvent.click(await screen.findByRole("button", { name: "Repair 1 rows" }));
    const dialog = within(screen.getByRole("dialog"));
    await userEvent.type(dialog.getByPlaceholderText("Nav09Endpoint"), "Nav09Endpoint");
    await userEvent.click(dialog.getByRole("button", { name: "Repair 1 rows" }));

    await waitFor(() => expect(reconcileMock).toHaveBeenCalledTimes(1));
    expect(reconcileMock.mock.calls[0][0]).toBe("Nav09Endpoint");
    expect(reconcileMock.mock.calls[0][1].enqueuedBefore).toBeInstanceOf(Date);
    expect(await screen.findByText("succeeded")).toBeTruthy();
    // The card re-reads the preview, which now has nothing left.
    await waitFor(() => expect(previewMock).toHaveBeenCalledTimes(2));
    expect(
      await screen.findByText("No Pending rows before that cut-off."),
    ).toBeTruthy();
  }, 15_000);

  it("explains a too-recent cut-off rejected by the server", async () => {
    previewMock.mockRejectedValue({ status: 400, message: "Bad Request" });
    await renderCard();

    await userEvent.click(screen.getByRole("button", { name: "Preview" }));

    expect(
      await screen.findByText("The cut-off must be at least 15 minutes in the past."),
    ).toBeTruthy();
  }, 10_000);
});
