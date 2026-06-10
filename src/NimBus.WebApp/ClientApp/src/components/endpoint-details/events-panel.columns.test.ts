import { beforeEach, describe, expect, it } from "vitest";
import {
  EVENT_COLUMNS,
  HIDDEN_COLUMNS_STORAGE_KEY,
  getVisibleColumns,
  loadHiddenColumns,
  saveHiddenColumns,
} from "./events-panel";

const ALL_IDS = EVENT_COLUMNS.map((c) => c.id);

describe("EVENT_COLUMNS", () => {
  it("locks exactly the identity column (Event Id)", () => {
    const locked = EVENT_COLUMNS.filter((c) => c.locked).map((c) => c.id);
    expect(locked).toEqual(["eventId"]);
  });

  it("matches the events table's canonical column set", () => {
    expect(ALL_IDS).toEqual([
      "eventId",
      "pendingCount",
      "deferredCount",
      "status",
      "sessionId",
      "eventTypeId",
      "updated",
      "added",
    ]);
  });
});

describe("getVisibleColumns", () => {
  it("returns every column, in order, when nothing is hidden", () => {
    expect(getVisibleColumns(new Set()).map((c) => c.id)).toEqual(ALL_IDS);
  });

  it("drops hidden columns while preserving canonical order", () => {
    const visible = getVisibleColumns(new Set(["status", "added"]));
    expect(visible.map((c) => c.id)).toEqual([
      "eventId",
      "pendingCount",
      "deferredCount",
      "sessionId",
      "eventTypeId",
      "updated",
    ]);
  });

  it("never hides a locked column even if its id is in the hidden set", () => {
    const visible = getVisibleColumns(new Set(["eventId"]));
    expect(visible.map((c) => c.id)).toContain("eventId");
    expect(visible.map((c) => c.id)).toEqual(ALL_IDS);
  });
});

describe("hidden-column persistence (localStorage)", () => {
  // This jsdom setup ships no window.localStorage (the component guards with
  // `?.` for the same reason), so back the tests with a Map-based Storage stub.
  beforeEach(() => {
    const store = new Map<string, string>();
    Object.defineProperty(window, "localStorage", {
      configurable: true,
      value: {
        getItem: (key: string) => store.get(key) ?? null,
        setItem: (key: string, value: string) => store.set(key, String(value)),
        removeItem: (key: string) => store.delete(key),
        clear: () => store.clear(),
      },
    });
  });

  it("round-trips the hidden set through localStorage", () => {
    saveHiddenColumns(new Set(["status", "added"]));
    expect(loadHiddenColumns()).toEqual(new Set(["status", "added"]));
  });

  it("returns an empty set when nothing has been stored", () => {
    expect(loadHiddenColumns()).toEqual(new Set());
  });

  it("tolerates malformed stored JSON", () => {
    window.localStorage.setItem(HIDDEN_COLUMNS_STORAGE_KEY, "{not json");
    expect(loadHiddenColumns()).toEqual(new Set());
  });

  it("ignores non-array / non-string stored shapes", () => {
    window.localStorage.setItem(
      HIDDEN_COLUMNS_STORAGE_KEY,
      JSON.stringify({ status: true }),
    );
    expect(loadHiddenColumns()).toEqual(new Set());

    window.localStorage.setItem(
      HIDDEN_COLUMNS_STORAGE_KEY,
      JSON.stringify(["status", 42, null]),
    );
    expect(loadHiddenColumns()).toEqual(new Set(["status"]));
  });
});
