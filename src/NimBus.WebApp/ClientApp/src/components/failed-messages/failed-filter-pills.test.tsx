import { afterEach, describe, expect, it, vi } from "vitest";
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import * as api from "api-client";
import { EMPTY_FAILED_FILTER } from "functions/failed-messages.functions";
import FailedFilterPills, { FailedSearchBox } from "./failed-filter-pills";

vi.mock("api-client", async () => {
  const actual =
    await vi.importActual<typeof import("api-client")>("api-client");
  class FakeClient {
    getEndpointsAll = () => Promise.resolve(["Crm", "Erp"]);
    getEventTypes = () => Promise.resolve([]);
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

afterEach(cleanup);

describe("FailedFilterPills", () => {
  it("picks a time range and drops any list window", () => {
    const onApply = vi.fn();
    render(
      <FailedFilterPills
        value={{ ...EMPTY_FAILED_FILTER, windowStart: "x", windowEnd: "y" }}
        onApply={onApply}
        onReset={vi.fn()}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Time range" }));
    fireEvent.click(screen.getByRole("button", { name: "Last 7 days" }));

    expect(onApply).toHaveBeenCalledWith({
      period: api.Period._7d,
      bucket: "",
      windowStart: "",
      windowEnd: "",
    });
    // The popover closes after a pick.
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("toggles a status off from all three", () => {
    const onApply = vi.fn();
    render(
      <FailedFilterPills
        value={EMPTY_FAILED_FILTER}
        statusCounts={{ Failed: 3, DeadLettered: 1, Unsupported: 0 }}
        onApply={onApply}
        onReset={vi.fn()}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Status" }));
    const dialog = screen.getByRole("dialog", { name: "Status" });
    fireEvent.click(within(dialog).getAllByRole("checkbox")[1]);

    expect(onApply).toHaveBeenCalledWith({
      status: ["Failed", "Unsupported"],
    });
  });

  it("sets and removes the publisher filter", async () => {
    const onApply = vi.fn();
    const { rerender } = render(
      <FailedFilterPills
        value={EMPTY_FAILED_FILTER}
        onApply={onApply}
        onReset={vi.fn()}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Add filter" }));
    const from = screen.getByLabelText("From (publisher)");
    await waitFor(() => expect(within(from).getByText("Crm")).toBeTruthy());
    fireEvent.change(from, { target: { value: "Crm" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    expect(onApply).toHaveBeenCalledWith({ from: "Crm", to: "" });

    rerender(
      <FailedFilterPills
        value={{ ...EMPTY_FAILED_FILTER, from: "Crm" }}
        onApply={onApply}
        onReset={vi.fn()}
      />,
    );
    fireEvent.click(
      screen.getByRole("button", { name: "Remove From (publisher)" }),
    );
    expect(onApply).toHaveBeenLastCalledWith({ from: "" });
  });

  it("offers Reset only when something is filtered", () => {
    const onReset = vi.fn();
    render(
      <FailedFilterPills
        value={EMPTY_FAILED_FILTER}
        onApply={vi.fn()}
        onReset={onReset}
      />,
    );
    expect(screen.queryByRole("button", { name: "Reset filters" })).toBeNull();

    cleanup();
    render(
      <FailedFilterPills
        value={{ ...EMPTY_FAILED_FILTER, errorText: "503" }}
        onApply={vi.fn()}
        onReset={onReset}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: "Reset filters" }));
    expect(onReset).toHaveBeenCalledTimes(1);
  });
});

describe("FailedSearchBox", () => {
  it("shows the applied fields and clears them", () => {
    const onApply = vi.fn();
    render(
      <FailedSearchBox
        value={{ ...EMPTY_FAILED_FILTER, sessionId: "s1", errorText: "503" }}
        onApply={onApply}
      />,
    );

    expect(
      (screen.getByLabelText("Search failures") as HTMLInputElement).value,
    ).toBe("session:s1 503");
    fireEvent.click(screen.getByRole("button", { name: "Clear search" }));
    expect(onApply).toHaveBeenCalledWith({
      eventId: "",
      lastMessageId: "",
      sessionId: "",
      errorText: "",
    });
  });
});
