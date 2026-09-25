import { describe, expect, it } from "vitest";
import * as api from "api-client";
import {
  EMPTY_FAILED_FILTER,
  deferredCountsBySession,
  errorTextOf,
  resolveWindow,
  selectedBucketWindow,
  toFailedSearchFilter,
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
    expect((w.to.getTime() - w.from.getTime()) / 3_600_000).toBe(24 * 7);
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

describe("selectedBucketWindow", () => {
  it("spans one bucket of the chosen range from the selected start", () => {
    const w = selectedBucketWindow({
      ...EMPTY_FAILED_FILTER,
      period: api.Period._7d,
      bucket: "2026-09-25T06:00:00.000Z",
    });
    expect(w?.to.toISOString()).toBe("2026-09-25T12:00:00.000Z");
  });

  it("is undefined without a valid selection", () => {
    expect(selectedBucketWindow(EMPTY_FAILED_FILTER)).toBeUndefined();
    expect(
      selectedBucketWindow({ ...EMPTY_FAILED_FILTER, bucket: "x" }),
    ).toBeUndefined();
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
