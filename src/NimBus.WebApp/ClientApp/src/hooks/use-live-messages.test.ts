import { act, cleanup, renderHook, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import * as api from "api-client";
import { useLiveMessages } from "./use-live-messages";

const search = vi.hoisted(() => vi.fn());
vi.mock("api-client", async () => {
  const actual = await vi.importActual<typeof api>("api-client");
  return {
    ...actual,
    CookieAuth: () => ({}),
    Client: class {
      postMessagesSearch = search;
    },
  };
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
  search.mockReset();
});
const message = (id: string) =>
  new api.Message({
    messageId: id,
    endpointId: "erp",
    from: "crm",
    to: "erp",
    eventTypeId: "Created",
  });

it("baselines history and animates each newly observed message only once", async () => {
  vi.useFakeTimers();
  search.mockResolvedValue({ messages: [message("old")] });
  const { result } = renderHook(() => useLiveMessages("", false));
  await act(async () => {
    await vi.advanceTimersByTimeAsync(0);
  });
  expect(result.current.messages).toHaveLength(1);
  expect(result.current.traffic).toEqual([]);
  search.mockResolvedValue({
    messages: [message("new"), message("new"), message("old")],
  });
  await act(async () => {
    await vi.advanceTimersByTimeAsync(5000);
  });
  expect(result.current.messages).toHaveLength(2);
  expect(result.current.traffic.map((m) => m.messageId)).toEqual(["new"]);
  await act(async () => {
    await vi.advanceTimersByTimeAsync(5000);
  });
  expect(result.current.traffic).toEqual([]);
});

it("does not let a stale filter response replace the current feed", async () => {
  let finishOld!: (response: { messages: api.Message[] }) => void;
  search.mockImplementationOnce(
    () =>
      new Promise((resolve) => {
        finishOld = resolve;
      }),
  );
  const { result, rerender } = renderHook(
    ({ filter }) => useLiveMessages(filter, false),
    { initialProps: { filter: "Old" } },
  );
  search.mockResolvedValue({ messages: [message("new")] });
  rerender({ filter: "New" });
  await waitFor(() =>
    expect(result.current.messages[0]?.messageId).toBe("new"),
  );
  await act(async () => finishOld({ messages: [message("old")] }));
  expect(result.current.messages[0]?.messageId).toBe("new");
  expect(search.mock.calls[1][0].filter.eventTypeId).toEqual(["New"]);
});

it("keeps the last feed after a refresh failure and stops fetching while paused", async () => {
  search.mockResolvedValue({ messages: [message("old")] });
  const { result, rerender } = renderHook(
    ({ paused }) => useLiveMessages("", paused),
    { initialProps: { paused: false } },
  );
  await waitFor(() => expect(result.current.messages).toHaveLength(1));
  rerender({ paused: true });
  expect(result.current.messages).toHaveLength(1);
  search.mockRejectedValue(new Error("unavailable"));
  rerender({ paused: false });
  await waitFor(() => expect(result.current.error).toBe(true));
  expect(result.current.messages).toHaveLength(1);
});
