import { describe, expect, it } from "vitest";
import { brushIndexes, windowOfIndexes } from "./failed-histogram";

const HOUR = 3_600_000;
const t0 = Date.parse("2026-09-25T00:00:00.000Z");
// Six hourly bars, 00:00 to 05:00.
const starts = [0, 1, 2, 3, 4, 5].map((h) => t0 + h * HOUR);

describe("brushIndexes", () => {
  it("spans every bar without a window", () => {
    expect(brushIndexes(starts, undefined)).toEqual({
      startIndex: 0,
      endIndex: 5,
    });
  });

  it("spans the bars whose start falls in the window", () => {
    expect(
      brushIndexes(starts, {
        from: new Date(t0 + 1 * HOUR),
        to: new Date(t0 + 4 * HOUR),
      }),
    ).toEqual({ startIndex: 1, endIndex: 3 });
  });

  it("falls back to every bar when the window holds none", () => {
    expect(
      brushIndexes(starts, {
        from: new Date(t0 + 10 * HOUR),
        to: new Date(t0 + 11 * HOUR),
      }),
    ).toEqual({ startIndex: 0, endIndex: 5 });
  });
});

describe("windowOfIndexes", () => {
  it("covers the brushed bars through the end of the last one", () => {
    const w = windowOfIndexes(starts, 60, 1, 3);
    expect(w?.from.toISOString()).toBe("2026-09-25T01:00:00.000Z");
    expect(w?.to.toISOString()).toBe("2026-09-25T04:00:00.000Z");
  });

  it("is undefined when the brush spans every bar", () => {
    expect(windowOfIndexes(starts, 60, 0, 5)).toBeUndefined();
    expect(windowOfIndexes([], 60, 0, 0)).toBeUndefined();
  });

  it("round-trips through brushIndexes", () => {
    const w = windowOfIndexes(starts, 60, 2, 4);
    expect(brushIndexes(starts, w)).toEqual({ startIndex: 2, endIndex: 4 });
  });
});
