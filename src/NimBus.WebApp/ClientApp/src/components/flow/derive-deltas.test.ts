import { describe, it, expect } from "vitest";
import { deriveDelta, applyToCounters } from "./derive-deltas";
import type {
  FlowActivityEvent,
  FlowActivityKind,
  FlowCounters,
  StatusSnapshot,
} from "./types";
import { MAX_DOTS_PER_DELTA } from "./types";

// Spec 020 Phase 1 — snapshot-diff derivation unit tests. deriveDelta turns a
// pair of consecutive EndpointStatusCount snapshots into semantic flow events;
// applyToCounters folds those events into the gauge-strip counters. Both are
// pure, so plain vitest with no jsdom is sufficient.

/** Builds a healthy zero-count snapshot; override only what the test varies. */
function snap(overrides: Partial<StatusSnapshot> = {}): StatusSnapshot {
  return {
    endpointId: "BillingEndpoint",
    failed: 0,
    deferred: 0,
    pending: 0,
    unsupported: 0,
    deadlettered: 0,
    storageOk: true,
    at: 1_000,
    ...overrides,
  };
}

/** Builds a minimal activity event for applyToCounters tests. */
function ev(kind: FlowActivityKind, count: number): FlowActivityEvent {
  return {
    endpointId: "BillingEndpoint",
    kind,
    count,
    dots: Math.min(count, MAX_DOTS_PER_DELTA),
    multiplier: count > MAX_DOTS_PER_DELTA ? count : 1,
    at: 1_000,
  };
}

function zeroCounters(): FlowCounters {
  return { published: 0, completed: 0, failed: 0, deferred: 0, deadlettered: 0 };
}

describe("deriveDelta — baseline handling", () => {
  it("treats the first-ever snapshot as baseline with no events", () => {
    const next = snap({ failed: 7, pending: 3 });
    const delta = deriveDelta(undefined, next);
    expect(delta).toEqual({
      endpointId: "BillingEndpoint",
      events: [],
      baseline: true,
    });
  });

  it("emits no events while storage is unavailable (counts unreliable)", () => {
    const prev = snap({ pending: 10 });
    const next = snap({ pending: 0, storageOk: false, at: 2_000 });
    const delta = deriveDelta(prev, next);
    expect(delta.events).toEqual([]);
    expect(delta.baseline).toBe(false);
  });

  it("re-baselines when storage recovers (no phantom mega-delta)", () => {
    // While the container was down the counts read as zeros; when it comes
    // back the jump from 0 → real counts must not animate as a burst.
    const prev = snap({ pending: 0, failed: 0, storageOk: false });
    const next = snap({ pending: 500, failed: 40, at: 2_000 });
    const delta = deriveDelta(prev, next);
    expect(delta.events).toEqual([]);
    expect(delta.baseline).toBe(true);
  });

  it("returns no events and baseline:false when nothing changed", () => {
    const prev = snap({ pending: 5, failed: 2, deferred: 1, deadlettered: 1 });
    const next = snap({
      pending: 5,
      failed: 2,
      deferred: 1,
      deadlettered: 1,
      at: 2_000,
    });
    const delta = deriveDelta(prev, next);
    expect(delta.events).toEqual([]);
    expect(delta.baseline).toBe(false);
  });
});

describe("deriveDelta — single-kind deltas", () => {
  it("pending rise → one 'arrived' event with the delta as count", () => {
    const delta = deriveDelta(snap({ pending: 2 }), snap({ pending: 6, at: 2_000 }));
    expect(delta.events).toEqual([
      {
        endpointId: "BillingEndpoint",
        kind: "arrived",
        count: 4,
        dots: 4,
        multiplier: 1,
        at: 2_000,
      },
    ]);
  });

  it("pending fall alone → one 'completed' event", () => {
    const delta = deriveDelta(snap({ pending: 6 }), snap({ pending: 1, at: 2_000 }));
    expect(delta.events).toEqual([
      {
        endpointId: "BillingEndpoint",
        kind: "completed",
        count: 5,
        dots: 5,
        multiplier: 1,
        at: 2_000,
      },
    ]);
  });

  it("failed rise → one 'failed' event", () => {
    const delta = deriveDelta(snap({ failed: 1 }), snap({ failed: 3, at: 2_000 }));
    expect(delta.events).toEqual([
      {
        endpointId: "BillingEndpoint",
        kind: "failed",
        count: 2,
        dots: 2,
        multiplier: 1,
        at: 2_000,
      },
    ]);
  });

  it("deferred rise → one 'deferred' event", () => {
    const delta = deriveDelta(snap({ deferred: 0 }), snap({ deferred: 4, at: 2_000 }));
    expect(delta.events).toEqual([
      {
        endpointId: "BillingEndpoint",
        kind: "deferred",
        count: 4,
        dots: 4,
        multiplier: 1,
        at: 2_000,
      },
    ]);
  });

  it("deferred fall → one 'released' event with absolute count", () => {
    const delta = deriveDelta(snap({ deferred: 4 }), snap({ deferred: 1, at: 2_000 }));
    expect(delta.events).toEqual([
      {
        endpointId: "BillingEndpoint",
        kind: "released",
        count: 3,
        dots: 3,
        multiplier: 1,
        at: 2_000,
      },
    ]);
  });

  it("deadletter rise → one 'deadlettered' event", () => {
    const delta = deriveDelta(
      snap({ deadlettered: 0 }),
      snap({ deadlettered: 2, at: 2_000 }),
    );
    expect(delta.events).toEqual([
      {
        endpointId: "BillingEndpoint",
        kind: "deadlettered",
        count: 2,
        dots: 2,
        multiplier: 1,
        at: 2_000,
      },
    ]);
  });
});

describe("deriveDelta — completed inference", () => {
  it("emits no 'completed' when the pending fall is fully explained by failures", () => {
    // 3 messages went pending → failed; they decrement pending AND increment
    // failed, so counting them as completions would double-book them.
    const delta = deriveDelta(
      snap({ pending: 10, failed: 0 }),
      snap({ pending: 7, failed: 3, at: 2_000 }),
    );
    expect(delta.events.map((e) => e.kind)).toEqual(["failed"]);
    expect(delta.events[0].count).toBe(3);
  });

  it("clamps completed at 0 when failures exceed the pending fall", () => {
    // failedΔ (5) > −pendingΔ (2): retried old failures can re-fail without
    // touching pending, so the inferred completed must clamp, never go negative.
    const delta = deriveDelta(
      snap({ pending: 10, failed: 0 }),
      snap({ pending: 8, failed: 5, at: 2_000 }),
    );
    expect(delta.events.map((e) => e.kind)).toEqual(["failed"]);
    expect(delta.events[0].count).toBe(5);
  });

  it("infers completed as the unexplained remainder of the pending fall", () => {
    // pending −10, of which 2 failed, 1 deadlettered → 7 genuinely completed.
    // The concurrent released (deferred −1) must NOT inflate completions.
    const delta = deriveDelta(
      snap({ pending: 10, failed: 0, deferred: 1, deadlettered: 0 }),
      snap({ pending: 0, failed: 2, deferred: 0, deadlettered: 1, at: 2_000 }),
    );
    const completed = delta.events.find((e) => e.kind === "completed");
    expect(completed?.count).toBe(7);
  });
});

describe("deriveDelta — mixed deltas and ordering", () => {
  it("emits one event per kind when arrivals and failures co-occur", () => {
    const delta = deriveDelta(
      snap({ pending: 1, failed: 0 }),
      snap({ pending: 3, failed: 4, at: 2_000 }),
    );
    expect(delta.events.map((e) => e.kind)).toEqual(["arrived", "failed"]);
    expect(delta.events.map((e) => e.count)).toEqual([2, 4]);
  });

  it("orders co-occurring kinds: arrived, completed, failed, deferred, released, deadlettered", () => {
    // pending −10 with failed +2, deferred −1, deadlettered +1 →
    // completed 7, failed 2, released 1, deadlettered 1, in canonical order.
    const delta = deriveDelta(
      snap({ pending: 10, failed: 0, deferred: 1, deadlettered: 0 }),
      snap({ pending: 0, failed: 2, deferred: 0, deadlettered: 1, at: 2_000 }),
    );
    expect(delta.events.map((e) => e.kind)).toEqual([
      "completed",
      "failed",
      "released",
      "deadlettered",
    ]);
  });

  it("stamps every event with next.at and the endpoint id", () => {
    const delta = deriveDelta(
      snap({ pending: 0, deferred: 0 }),
      snap({ pending: 3, deferred: 2, at: 4_242 }),
    );
    expect(delta.events.length).toBeGreaterThan(1);
    for (const event of delta.events) {
      expect(event.at).toBe(4_242);
      expect(event.endpointId).toBe("BillingEndpoint");
    }
  });
});

describe("deriveDelta — dot capping", () => {
  it("caps a burst at MAX_DOTS_PER_DELTA dots with the count as multiplier", () => {
    const delta = deriveDelta(snap({ pending: 0 }), snap({ pending: 500, at: 2_000 }));
    expect(delta.events).toEqual([
      {
        endpointId: "BillingEndpoint",
        kind: "arrived",
        count: 500,
        dots: MAX_DOTS_PER_DELTA,
        multiplier: 500,
        at: 2_000,
      },
    ]);
  });

  it("renders small deltas 1:1 with multiplier 1", () => {
    const delta = deriveDelta(snap({ failed: 0 }), snap({ failed: 3, at: 2_000 }));
    expect(delta.events[0].dots).toBe(3);
    expect(delta.events[0].multiplier).toBe(1);
  });
});

describe("applyToCounters", () => {
  it("maps each event kind onto its counter", () => {
    const result = applyToCounters(zeroCounters(), [
      ev("arrived", 5),
      ev("completed", 4),
      ev("failed", 3),
      ev("deferred", 2),
      ev("deadlettered", 1),
    ]);
    expect(result).toEqual({
      published: 5,
      completed: 4,
      failed: 3,
      deferred: 2,
      deadlettered: 1,
    });
  });

  it("accumulates on top of existing counter values", () => {
    const seeded: FlowCounters = {
      published: 100,
      completed: 90,
      failed: 5,
      deferred: 2,
      deadlettered: 1,
    };
    const result = applyToCounters(seeded, [ev("failed", 3), ev("arrived", 10)]);
    expect(result.failed).toBe(8);
    expect(result.published).toBe(110);
  });

  it("'released' advances no counter (resubmit vs skip is indistinguishable)", () => {
    const before = zeroCounters();
    const result = applyToCounters(before, [ev("released", 9)]);
    expect(result).toEqual(zeroCounters());
  });

  it("does not mutate the input counters and returns a new object", () => {
    const input = zeroCounters();
    const result = applyToCounters(input, [ev("failed", 3)]);
    expect(input).toEqual(zeroCounters());
    expect(result).not.toBe(input);
  });

  it("returns an equal copy for an empty event list", () => {
    const input: FlowCounters = {
      published: 1,
      completed: 2,
      failed: 3,
      deferred: 4,
      deadlettered: 5,
    };
    const result = applyToCounters(input, []);
    expect(result).toEqual(input);
    expect(result).not.toBe(input);
  });
});
