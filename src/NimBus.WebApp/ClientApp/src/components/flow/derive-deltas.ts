import type {
  EndpointDelta,
  FlowActivityEvent,
  FlowActivityKind,
  FlowCounters,
  StatusSnapshot,
} from "./types";
import { MAX_DOTS_PER_DELTA } from "./types";

// Spec 020 Phase 1 — delta-to-events derivation. The only live signal today is
// aggregate ("endpoint X's counts are now {…}"), so this module diffs
// consecutive snapshots per endpoint and emits semantic FlowActivityEvents for
// the animator, the activity log, and the counters. Pure by design: no clock,
// no randomness, no I/O — the `at` on every event comes from the snapshot
// itself, which keeps the whole pipeline replayable in tests.

export function deriveDelta(
  prev: StatusSnapshot | undefined,
  next: StatusSnapshot,
): EndpointDelta {
  // First snapshot for this endpoint: there is nothing to diff against, and
  // animating absolute counts as if they "just happened" would be fabrication.
  // The page uses `baseline: true` to seed silently (FR-004).
  if (prev === undefined) {
    return { endpointId: next.endpointId, events: [], baseline: true };
  }

  // While the storage provider is degraded the counts read as zeros (or
  // stale) — diffing them would animate noise. Emit nothing; the node-border
  // state machine handles the visual "storage down" treatment instead.
  if (!next.storageOk) {
    return { endpointId: next.endpointId, events: [], baseline: false };
  }

  // Storage just came back: the previous snapshot's counts were unreliable,
  // so the jump from "zeros while down" to "real counts" must re-baseline
  // rather than render as a phantom mega-burst of arrivals/failures.
  if (!prev.storageOk) {
    return { endpointId: next.endpointId, events: [], baseline: true };
  }

  const failedDelta = next.failed - prev.failed;
  const deferredDelta = next.deferred - prev.deferred;
  const pendingDelta = next.pending - prev.pending;
  const deadletteredDelta = next.deadlettered - prev.deadlettered;

  // Completed is the one outcome with no counter of its own — we infer it
  // from the pending fall. A pending message that fails / defers /
  // deadletters ALSO decrements pending, so those transitions must be
  // subtracted out or they'd double-count as completions. Clamp at 0 because
  // retried old failures can raise `failed` without touching `pending`,
  // making the subtraction overshoot.
  const completed =
    pendingDelta < 0
      ? Math.max(
          0,
          -pendingDelta -
            Math.max(0, failedDelta) -
            Math.max(0, deferredDelta) -
            Math.max(0, deadletteredDelta),
        )
      : 0;

  // Several kinds can legitimately co-occur in one 5 s diff (e.g. arrivals
  // while older messages fail). Emit one event per kind in a fixed canonical
  // order so the animator, log, and tests are deterministic across runs.
  const byKind: ReadonlyArray<readonly [FlowActivityKind, number]> = [
    ["arrived", Math.max(0, pendingDelta)],
    ["completed", completed],
    ["failed", Math.max(0, failedDelta)],
    ["deferred", Math.max(0, deferredDelta)],
    ["released", Math.max(0, -deferredDelta)],
    ["deadlettered", Math.max(0, deadletteredDelta)],
  ];

  const events: FlowActivityEvent[] = [];
  for (const [kind, count] of byKind) {
    if (count <= 0) continue;
    // FR-009: never flood the canvas — a burst renders at most
    // MAX_DOTS_PER_DELTA dots and carries the real magnitude as a "×N"
    // badge; counters and the log always use the true count.
    const dots = Math.min(count, MAX_DOTS_PER_DELTA);
    events.push({
      endpointId: next.endpointId,
      kind,
      count,
      dots,
      multiplier: count > dots ? count : 1,
      at: next.at,
    });
  }

  return { endpointId: next.endpointId, events, baseline: false };
}

/**
 * Which gauge-strip counter each event kind advances. "released" is
 * deliberately absent: a deferred fall only tells us messages left the
 * parking strip — Phase 1 cannot distinguish a resubmit (which should count
 * as re-published work) from a skip (which should not), so it advances no
 * counter and only appears in the activity log.
 */
const COUNTER_BY_KIND: Partial<Record<FlowActivityKind, keyof FlowCounters>> = {
  arrived: "published",
  completed: "completed",
  failed: "failed",
  deferred: "deferred",
  deadlettered: "deadlettered",
};

/**
 * Folds derived events into the counters. Pure: always returns a fresh
 * object so React state updates (and the 60 s reconcile pass) can compare by
 * reference without defensive copying at the call site.
 */
export function applyToCounters(
  counters: FlowCounters,
  events: FlowActivityEvent[],
): FlowCounters {
  const next: FlowCounters = { ...counters };
  for (const event of events) {
    const key = COUNTER_BY_KIND[event.kind];
    if (key !== undefined) {
      next[key] += event.count;
    }
  }
  return next;
}
