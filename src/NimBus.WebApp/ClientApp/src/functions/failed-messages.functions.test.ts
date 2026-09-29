import { describe, expect, it } from "vitest";
import * as api from "api-client";
import {
  EMPTY_FAILED_FILTER,
  deferredCountsBySession,
  errorTextOf,
  parseSearchQuery,
  resolveWindow,
  searchQueryOf,
  selectedWindow,
  toFailedSearchFilter,
  toggleStatus,
  windowParams,
} from "./failed-messages.functions";

describe("resolveWindow", () => {
  it("ends at the end of the bucket holding now, like the server", () => {
    const now = new Date("2026-09-25T10:17:30Z");

    const day = resolveWindow(api.Period._1d, now);
    expect(day.to.toISOString()).toBe("2026-09-25T11:00:00.000Z");
    expect(day.from.toISOString()).toBe("2026-09-24T11:00:00.000Z");

    const hour = resolveWindow(api.Period._1h, now);
    expect(hour.to.toISOString()).toBe("2026-09-25T10:20:00.000Z");
    expect(hour.from.toISOString()).toBe("2026-09-25T09:20:00.000Z");
  });

  it("falls back to the default range for an unknown period", () => {
    const now = new Date("2026-09-25T10:17:30Z");
    const w = resolveWindow("nonsense", now);
    expect((w.to.getTime() - w.from.getTime()) / 3_600_000).toBe(24);
  });
});

describe("toFailedSearchFilter", () => {
  it("maps the applied values and keeps only failure statuses", () => {
    const filter = toFailedSearchFilter({
      ...EMPTY_FAILED_FILTER,
      endpointId: ["Crm"],
      status: ["DeadLettered", "Completed"],
      errorText: "  timeout ",
      eventId: "",
    });

    expect(filter.endpointIds).toEqual(["Crm"]);
    expect(filter.statuses).toEqual([api.Statuses.DeadLettered]);
    expect(filter.errorText).toBe("timeout");
    expect(filter.eventId).toBeUndefined();
    expect(filter.updatedAtFrom).toBeUndefined();
  });

  it("narrows UpdatedAt to a half-open window", () => {
    const from = new Date("2026-09-25T10:00:00Z");
    const to = new Date("2026-09-25T11:00:00Z");

    const filter = toFailedSearchFilter(EMPTY_FAILED_FILTER, { from, to });

    expect(filter.updatedAtFrom?.toISOString()).toBe(
      "2026-09-25T10:00:00.000Z",
    );
    expect(filter.updatedAtTo?.toISOString()).toBe("2026-09-25T10:59:59.999Z");
  });
});

describe("selectedWindow", () => {
  it("prefers the window params", () => {
    const w = selectedWindow({
      ...EMPTY_FAILED_FILTER,
      windowStart: "2026-09-25T06:00:00.000Z",
      windowEnd: "2026-09-25T18:00:00.000Z",
      bucket: "2026-09-24T00:00:00.000Z",
    });
    expect(w?.from.toISOString()).toBe("2026-09-25T06:00:00.000Z");
    expect(w?.to.toISOString()).toBe("2026-09-25T18:00:00.000Z");
  });

  it("falls back to one bucket of the range for a legacy bucket link", () => {
    const w = selectedWindow({
      ...EMPTY_FAILED_FILTER,
      period: api.Period._7d,
      bucket: "2026-09-25T06:00:00.000Z",
    });
    expect(w?.from.toISOString()).toBe("2026-09-25T06:00:00.000Z");
    expect(w?.to.toISOString()).toBe("2026-09-25T12:00:00.000Z");
  });

  it("is undefined without a valid window", () => {
    expect(selectedWindow(EMPTY_FAILED_FILTER)).toBeUndefined();
    expect(
      selectedWindow({ ...EMPTY_FAILED_FILTER, bucket: "x" }),
    ).toBeUndefined();
    expect(
      selectedWindow({
        ...EMPTY_FAILED_FILTER,
        windowStart: "2026-09-25T18:00:00.000Z",
        windowEnd: "2026-09-25T06:00:00.000Z",
      }),
    ).toBeUndefined();
  });
});

describe("windowParams", () => {
  it("writes a window as ISO params and clears the legacy bucket", () => {
    expect(
      windowParams({
        from: new Date("2026-09-25T06:00:00Z"),
        to: new Date("2026-09-25T12:00:00Z"),
      }),
    ).toEqual({
      bucket: "",
      windowStart: "2026-09-25T06:00:00.000Z",
      windowEnd: "2026-09-25T12:00:00.000Z",
    });
    expect(windowParams(undefined)).toEqual({
      bucket: "",
      windowStart: "",
      windowEnd: "",
    });
  });
});

describe("parseSearchQuery", () => {
  const guid = "b5ec63b6-8a1f-4e02-9c7d-4f3a2b1e0d96";

  it("reads prefixed tokens into their fields and the rest as error text", () => {
    expect(
      parseSearchQuery("event:e1 message:m1 session:s1 503 timeout"),
    ).toEqual({
      eventId: "e1",
      lastMessageId: "m1",
      sessionId: "s1",
      errorText: "503 timeout",
    });
  });

  it("treats a bare GUID as an event ID", () => {
    expect(parseSearchQuery(`  ${guid} `)).toEqual({
      eventId: guid,
      lastMessageId: "",
      sessionId: "",
      errorText: "",
    });
  });

  it("keeps quoted text as error text, even when it looks like a GUID or a prefix", () => {
    expect(parseSearchQuery(`"${guid}"`).errorText).toBe(guid);
    expect(parseSearchQuery(`"event:x failed"`)).toEqual({
      eventId: "",
      lastMessageId: "",
      sessionId: "",
      errorText: "event:x failed",
    });
    expect(parseSearchQuery('session:"a b"').sessionId).toBe("a b");
  });

  it("clears every field for an empty query", () => {
    expect(parseSearchQuery("   ")).toEqual({
      eventId: "",
      lastMessageId: "",
      sessionId: "",
      errorText: "",
    });
  });
});

describe("searchQueryOf", () => {
  it("round-trips through parseSearchQuery", () => {
    const cases = [
      { eventId: "e1", lastMessageId: "", sessionId: "s 1", errorText: "503" },
      { eventId: "", lastMessageId: "m1", sessionId: "", errorText: "a:b" },
      {
        eventId: "",
        lastMessageId: "",
        sessionId: "",
        errorText: "b5ec63b6-8a1f-4e02-9c7d-4f3a2b1e0d96",
      },
      {
        eventId: "b5ec63b6-8a1f-4e02-9c7d-4f3a2b1e0d96",
        lastMessageId: "",
        sessionId: "",
        errorText: "",
      },
    ];
    for (const fields of cases) {
      const query = searchQueryOf({ ...EMPTY_FAILED_FILTER, ...fields });
      expect(parseSearchQuery(query)).toEqual(fields);
    }
  });

  it("is empty when no search field is set", () => {
    expect(searchQueryOf(EMPTY_FAILED_FILTER)).toBe("");
  });
});

describe("toggleStatus", () => {
  it("switches one status off from all, and back to all", () => {
    const off = toggleStatus([], "Failed");
    expect(off).toEqual(["DeadLettered", "Unsupported"]);
    expect(toggleStatus(off, "Failed")).toEqual([]);
  });
});

describe("errorTextOf", () => {
  it("prefers the handler error, then the dead-letter description", () => {
    const withError = new api.Event({
      messageContent: new api.MessageContent({
        errorContent: new api.ErrorContent({ errorText: "boom" }),
      }),
      deadLetterErrorDescription: "dl",
    });
    expect(errorTextOf(withError)).toBe("boom");
    expect(
      errorTextOf(new api.Event({ deadLetterErrorDescription: "dl" })),
    ).toBe("dl");
  });

  it("labels an Unsupported event without an error", () => {
    const unsupported = new api.Event({
      resolutionStatus: api.ResolutionStatus.Unsupported,
    });
    expect(errorTextOf(unsupported)).toMatch(/^Unsupported:/);
  });
});

describe("deferredCountsBySession", () => {
  it("counts deferred events per session", () => {
    expect(
      deferredCountsBySession(["e1_s1", "e2_s1", "e3_s2", "broken"]),
    ).toEqual({ s1: 2, s2: 1 });
  });
});
