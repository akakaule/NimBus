import { describe, expect, it } from "vitest";
import {
  EMPTY_ADVANCED_FILTERS,
  advancedChips,
  advancedCount,
  type AdvancedFilters,
} from "./advanced-filters";

const full: AdvancedFilters = {
  updatedFrom: "2026-06-01T10:00",
  updatedTo: "2026-06-02T10:00",
  addedFrom: "2026-05-01T08:00",
  addedTo: "2026-05-02T08:00",
  payload: "orderId:4471",
};

describe("advancedChips / advancedCount", () => {
  it("returns no chips when nothing is set", () => {
    expect(advancedChips(EMPTY_ADVANCED_FILTERS)).toEqual([]);
    expect(advancedCount(EMPTY_ADVANCED_FILTERS)).toBe(0);
  });

  it("emits one chip per active field, in canonical order, with labels", () => {
    expect(advancedChips(full).map((c) => c.key)).toEqual([
      "updatedFrom",
      "updatedTo",
      "addedFrom",
      "addedTo",
      "payload",
    ]);
    expect(advancedChips(full).map((c) => c.label)).toEqual([
      "Updated from",
      "Updated to",
      "Added from",
      "Added to",
      "Payload",
    ]);
    expect(advancedCount(full)).toBe(5);
  });

  it("formats datetime values with a space and shows payload verbatim", () => {
    const chips = advancedChips(full);
    expect(chips.find((c) => c.key === "updatedFrom")?.display).toBe(
      "2026-06-01 10:00",
    );
    expect(chips.find((c) => c.key === "payload")?.display).toBe("orderId:4471");
  });

  it("skips empty / whitespace-only fields (partial selection)", () => {
    const partial: AdvancedFilters = {
      ...EMPTY_ADVANCED_FILTERS,
      updatedFrom: "2026-06-01T10:00",
      payload: "   ",
    };
    expect(advancedChips(partial).map((c) => c.key)).toEqual(["updatedFrom"]);
    expect(advancedCount(partial)).toBe(1);
  });

  it("supports a payload-only filter", () => {
    const p: AdvancedFilters = { ...EMPTY_ADVANCED_FILTERS, payload: "abc" };
    expect(advancedChips(p).map((c) => c.key)).toEqual(["payload"]);
    expect(advancedCount(p)).toBe(1);
  });
});
