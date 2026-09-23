import { afterEach, describe, expect, it, vi } from "vitest";
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import * as api from "api-client";
import DeferredRecovery from "./deferred-recovery";

const mocks = vi.hoisted(() => ({
  inspect: vi.fn(),
  skip: vi.fn(),
  toast: vi.fn(),
}));
vi.mock("api-client", async () => ({
  ...(await vi.importActual<typeof import("api-client")>("api-client")),
  Client: class {
    getDeferredInspection = mocks.inspect;
    postSkipDeferredTracking = mocks.skip;
  },
  CookieAuth: () => ({}),
}));
vi.mock("components/ui/toast", () => ({
  useToast: () => ({ addToast: mocks.toast }),
}));
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function result(canSkip = true) {
  return new api.DeferredInspection({
    rowVersion: "version-1",
    canSkip,
    historyOutcome: "Unknown",
    historyDetail:
      "No recorded terminal outcome for this endpoint and session.",
    skipDetail: canSkip
      ? "No matching messages found."
      : "Broker inspection is incomplete.",
    brokerChecks: [
      new api.DeferredBrokerCheck({
        location: "Deferred",
        status: canSkip
          ? api.DeferredBrokerCheckStatus.NotFound
          : api.DeferredBrokerCheckStatus.Unknown,
      }),
    ],
  });
}
const props = {
  endpointId: "crm",
  eventId: "event",
  onResolved: vi.fn().mockResolvedValue(undefined),
};

describe("DeferredRecovery", () => {
  it("keeps unknown processing history separate from broker absence and requires a reason", async () => {
    mocks.inspect.mockResolvedValue(result());
    render(<DeferredRecovery {...props} />);
    fireEvent.click(
      screen.getByRole("button", { name: "Check deferred message" }),
    );
    await screen.findByText(
      "No recorded terminal outcome for this endpoint and session.",
    );
    fireEvent.click(
      screen.getByRole("button", { name: "Skip tracking record" }),
    );
    const confirm = screen.getByRole("button", {
      name: "Confirm skip",
    }) as HTMLButtonElement;
    expect(confirm.disabled).toBe(true);
    fireEvent.change(screen.getByLabelText("Reason"), {
      target: { value: "Verified externally" },
    });
    mocks.skip.mockResolvedValue(
      new api.DeferredSkipResult({ auditRecorded: true }),
    );
    fireEvent.click(confirm);
    await waitFor(() =>
      expect(mocks.skip).toHaveBeenCalledWith(
        "crm",
        "event",
        expect.objectContaining({
          rowVersion: "version-1",
          reason: "Verified externally",
        }),
      ),
    );
    await waitFor(() => expect(props.onResolved).toHaveBeenCalled());
  });

  it("blocks skip on incomplete inspection", async () => {
    mocks.inspect.mockResolvedValue(result(false));
    render(<DeferredRecovery {...props} />);
    fireEvent.click(
      screen.getByRole("button", { name: "Check deferred message" }),
    );
    await screen.findByText("Broker inspection is incomplete.");
    expect(
      screen.queryByRole("button", { name: "Skip tracking record" }),
    ).toBeNull();
  });

  it("does not show an older event's inspection after navigation", async () => {
    let resolve!: (value: api.DeferredInspection) => void;
    mocks.inspect.mockReturnValue(
      new Promise<api.DeferredInspection>((r) => {
        resolve = r;
      }),
    );
    const view = render(<DeferredRecovery {...props} />);
    fireEvent.click(
      screen.getByRole("button", { name: "Check deferred message" }),
    );
    view.rerender(<DeferredRecovery {...props} eventId="other-event" />);
    await act(async () => resolve(result()));
    expect(
      screen.queryByText(
        "No recorded terminal outcome for this endpoint and session.",
      ),
    ).toBeNull();
  });

  it("discards eligibility after a failed recheck or conflict", async () => {
    mocks.inspect
      .mockResolvedValueOnce(result())
      .mockRejectedValueOnce(new Error("offline"));
    render(<DeferredRecovery {...props} />);
    fireEvent.click(
      screen.getByRole("button", { name: "Check deferred message" }),
    );
    await screen.findByRole("button", { name: "Skip tracking record" });
    fireEvent.click(screen.getByRole("button", { name: "Check again" }));
    await screen.findByRole("alert");
    expect(
      screen.queryByRole("button", { name: "Skip tracking record" }),
    ).toBeNull();
  });
});
