import { describe, expect, it } from "vitest";
import { customRangeError } from "./time-range.functions";

describe("customRangeError", () => {
  it("explains why a range is refused", () => {
    const at = (iso: string) => new Date(iso);
    expect(
      customRangeError(at("2026-09-20T08:00Z"), at("2026-09-21T08:00Z")),
    ).toBeUndefined();
    expect(
      customRangeError(at("2026-09-21T08:00Z"), at("2026-09-21T08:00Z")),
    ).toMatch(/before the end/);
    expect(
      customRangeError(at("2026-06-01T00:00Z"), at("2026-09-21T00:00Z")),
    ).toMatch(/at most 90 days/);
    expect(customRangeError(at(""), at("2026-09-21T00:00Z"))).toMatch(
      /Enter a start/,
    );
  });
});
