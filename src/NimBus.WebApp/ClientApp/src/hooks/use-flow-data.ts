import { useCallback, useEffect, useRef, useState } from "react";
import * as api from "api-client";
import { useTopologyData } from "components/topology/use-topology-data";
import { applyToCounters, deriveDelta } from "components/flow/derive-deltas";
import {
  LOG_MAX,
  POLL_MS,
  RECONCILE_MS,
} from "components/flow/types";
import type {
  ActivityEntry,
  ConnectionMode,
  FlowActivityEvent,
  FlowActivityKind,
  FlowCounters,
  StatusSnapshot,
} from "components/flow/types";
import type { TopologyData } from "components/topology/types";
import {
  subscribeEndpointUpdates,
} from "lib/grid-events-connection";

/**
 * Activity-log verb per event kind. Exported so the page can rebuild the
 * humanized line around a linked endpoint name without duplicating the map.
 */
export const FLOW_KIND_VERB: Record<FlowActivityKind, string> = {
  arrived: "arrived",
  completed: "completed",
  failed: "failed",
  deferred: "deferred",
  released: "released",
  deadlettered: "dead-lettered",
};

export interface UseFlowDataOptions {
  period: api.Period;
  /**
   * While true, derived activity buffers instead of advancing counters, log,
   * or dots; the buffer applies in one pass on resume (spec US3-1). Snapshot
   * tracking continues throughout, so parking strips stay authoritative and
   * nothing is lost or double-counted.
   */
  paused: boolean;
  /** Called with derived activity for the page to animate. NOT called while document.hidden (dots dropped; counters/log still update). */
  onActivity: (events: FlowActivityEvent[]) => void;
}

export interface UseFlowDataResult {
  topology: TopologyData | undefined;
  topologyLoading: boolean;
  topologyError?: string;
  counters: FlowCounters;
  log: ActivityEntry[];
  mode: ConnectionMode; // "connecting" | "live" | "polling"
  /** Latest authoritative snapshot per endpoint — drives parking strips (FR-006). */
  snapshots: Record<string, StatusSnapshot>;
}

const ZERO_COUNTERS: FlowCounters = {
  published: 0,
  completed: 0,
  failed: 0,
  deferred: 0,
  deadlettered: 0,
};

/**
 * Bound on derived events held back while paused (US3-1). On overflow the
 * oldest events are evicted: their dots and log lines condense into a single
 * marker line on resume, but their counter contributions are carried forward
 * so the gauges never lose counts ("buffered deltas apply ... without being
 * lost").
 */
const PAUSE_BUFFER_MAX = 500;

function addCounters(a: FlowCounters, b: FlowCounters): FlowCounters {
  return {
    published: a.published + b.published,
    completed: a.completed + b.completed,
    failed: a.failed + b.failed,
    deferred: a.deferred + b.deferred,
    deadlettered: a.deadlettered + b.deadlettered,
  };
}

/**
 * Boundary conversion to the pure modules' StatusSnapshot. Handles BOTH
 * sources feeding the pipeline:
 *  - hub payloads: raw camelCase JSON, `eventTime` is an ISO string
 *  - polled api.EndpointStatusCount: `eventTime` is a moment instance
 * Defensive throughout — missing numbers read as 0 and a missing/garbled
 * eventTime falls back to receipt time, so a malformed broadcast degrades to
 * a slightly mistimed snapshot instead of NaN propagating into the diff.
 */
function toSnapshot(raw: unknown): StatusSnapshot | undefined {
  if (raw === null || typeof raw !== "object") return undefined;
  const r = raw as Record<string, unknown>;
  const endpointId = r["endpointId"];
  if (typeof endpointId !== "string" || endpointId.length === 0) {
    return undefined;
  }

  const num = (value: unknown): number =>
    typeof value === "number" && Number.isFinite(value) ? value : 0;

  const eventTime = r["eventTime"];
  let at: number;
  if (typeof eventTime === "string") {
    at = Date.parse(eventTime) || Date.now();
  } else if (
    eventTime !== null &&
    typeof eventTime === "object" &&
    typeof (eventTime as { valueOf?: unknown }).valueOf === "function"
  ) {
    const ms = Number((eventTime as { valueOf(): unknown }).valueOf());
    at = Number.isFinite(ms) && ms > 0 ? ms : Date.now();
  } else {
    at = Date.now();
  }

  return {
    endpointId,
    failed: num(r["failedCount"]),
    deferred: num(r["deferredCount"]),
    pending: num(r["pendingCount"]),
    unsupported: num(r["unsupportedCount"]),
    deadlettered: num(r["deadletterCount"]),
    storageOk: r["storageStatus"] === "ok",
    subscriptionStatus:
      typeof r["subscriptionStatus"] === "string"
        ? r["subscriptionStatus"]
        : undefined,
    at,
  };
}

/**
 * Live data feed for the Flow page (spec 020, Phase 1). One pipeline serves
 * both signal sources: hub `endpointupdate` events and status polls each
 * become StatusSnapshots, diff against the previous snapshot per endpoint
 * (deriveDelta), and fan out to counters, the activity log, the parking-strip
 * snapshots, and the page's animator callback.
 *
 *  - On mount a full status poll BASELINES every endpoint: deriveDelta marks
 *    first sightings baseline (no animation) but the snapshots populate the
 *    parking strips immediately (US2 acceptance 1).
 *  - While the hub is connected ("live") fallback polling stays off; when it
 *    drops we poll every POLL_MS ("polling") and re-upgrade on reconnect.
 *  - A reconcile poll runs every RECONCILE_MS regardless of mode (missed
 *    broadcasts drift the counters, FR-005). Its snapshots flow through the
 *    same pipeline so corrections animate naturally; it skips a tick when
 *    fallback polling already covered the window.
 *  - Counters seed from /api/metrics/overview for the selected period and
 *    advance with derived deltas. Period change re-seeds the counters only —
 *    prevRef tracks status counts, which are unrelated to the metrics window.
 *  - While `paused`, derived activity buffers (bounded) instead of advancing
 *    counters/log/dots; the resume edge applies the whole buffer in one pass
 *    (US3-1). Snapshots keep updating regardless — FR-006 strips stay live.
 */
export function useFlowData(opts: UseFlowDataOptions): UseFlowDataResult {
  const { period, paused } = opts;
  const {
    data: topology,
    loading: topologyLoading,
    error: topologyError,
  } = useTopologyData({ period });

  const [counters, setCounters] = useState<FlowCounters>(ZERO_COUNTERS);
  const [log, setLog] = useState<ActivityEntry[]>([]);
  const [mode, setMode] = useState<ConnectionMode>("connecting");
  const [snapshots, setSnapshots] = useState<Record<string, StatusSnapshot>>(
    {},
  );

  // Refs because these mutate on every event/poll but only need to bake into
  // state when a processing pass actually produces something to render.
  const clientRef = useRef<api.Client | null>(null);
  const prevRef = useRef(new Map<string, StatusSnapshot>());
  const snapshotsRef = useRef<Record<string, StatusSnapshot>>({});
  const pollInFlightRef = useRef(false);
  const lastPollAtRef = useRef(0);
  /** Mount-time baseline poll; the counter seed awaits it so the deferred /
   *  dead-letter sums read real snapshots instead of an empty map. */
  const baselinePollRef = useRef<Promise<void> | null>(null);

  // The animator callback lives in a ref so the pipeline stays stable across
  // page re-renders (the page passes a useCallback, but we don't rely on it).
  const onActivityRef = useRef(opts.onActivity);
  useEffect(() => {
    onActivityRef.current = opts.onActivity;
  });

  const logSeqRef = useRef(0);

  const makeEntry = useCallback(
    (event: FlowActivityEvent): ActivityEntry => ({
      id: `flow-${++logSeqRef.current}`,
      at: event.at,
      endpointId: event.endpointId,
      kind: event.kind,
      count: event.count,
      message: `${event.endpointId}: ${event.count} ${FLOW_KIND_VERB[event.kind]}`,
    }),
    [],
  );

  // Pause buffering (US3-1). The pipeline reads `pausedRef` — not the prop —
  // so hub events arriving between renders see the current value. Evicted
  // overflow keeps a count (for the condensed log line) and a counter fold
  // (so the gauges still reconcile exactly on resume).
  const pausedRef = useRef(paused);
  const pauseBufferRef = useRef<FlowActivityEvent[]>([]);
  const droppedWhilePausedRef = useRef(0);
  const droppedCountersRef = useRef<FlowCounters>(ZERO_COUNTERS);

  /**
   * Shared processing pass for hub events and poll results. Batched: one
   * setCounters + one setLog + one setSnapshots per pass regardless of how
   * many endpoints changed (NFR-002 throttling posture).
   */
  const processSnapshots = useCallback((snaps: StatusSnapshot[]) => {
    if (snaps.length === 0) return;

    const events: FlowActivityEvent[] = [];
    const changed: Record<string, StatusSnapshot> = {};
    for (const snap of snaps) {
      const delta = deriveDelta(prevRef.current.get(snap.endpointId), snap);
      prevRef.current.set(snap.endpointId, snap);
      changed[snap.endpointId] = snap;
      events.push(...delta.events);
    }

    // Snapshots are authoritative even when nothing animated (FR-006 — the
    // parking strip must track DeferredCount, not just deferred deltas).
    snapshotsRef.current = { ...snapshotsRef.current, ...changed };
    setSnapshots(snapshotsRef.current);

    if (events.length === 0) return;

    // US3-1: while paused nothing advances visibly. prevRef above ALREADY
    // moved on, so diffing keeps tracking the live stream — the buffer holds
    // exactly the deltas the user hasn't seen yet, ready to apply in one
    // pass on resume without loss or double-counting.
    if (pausedRef.current) {
      const buffer = pauseBufferRef.current;
      buffer.push(...events);
      const overflow = buffer.length - PAUSE_BUFFER_MAX;
      if (overflow > 0) {
        const evicted = buffer.splice(0, overflow);
        droppedWhilePausedRef.current += evicted.length;
        droppedCountersRef.current = applyToCounters(
          droppedCountersRef.current,
          evicted,
        );
      }
      return;
    }

    const entries = events.map(makeEntry);
    setCounters((prev) => applyToCounters(prev, events));
    setLog((prev) => [...entries, ...prev].slice(0, LOG_MAX));

    // Hidden tab: drop the dots (rAF is suspended anyway — spawning would
    // just queue a catch-up storm); counters and log above still updated.
    if (!document.hidden) {
      onActivityRef.current(events);
    }
  }, [makeEntry]);

  /**
   * Resume pass: everything that accumulated while paused lands as ONE
   * counter fold, ONE log append, and ONE onActivity batch (the animator's
   * in-flight cap turns excess dots into edge pulses; the hidden-tab rule
   * still applies). Buffer-evicted events surface as a single condensed
   * marker line so the operator knows the log isn't gapless.
   */
  const flushPauseBuffer = useCallback(() => {
    const buffered = pauseBufferRef.current;
    const dropped = droppedWhilePausedRef.current;
    const carried = droppedCountersRef.current;
    if (buffered.length === 0 && dropped === 0) return;
    pauseBufferRef.current = [];
    droppedWhilePausedRef.current = 0;
    droppedCountersRef.current = ZERO_COUNTERS;

    setCounters((prev) => applyToCounters(addCounters(prev, carried), buffered));

    // The buffer accumulated in arrival order; the log reads newest-first,
    // so it flushes reversed. The condensed marker stands in for the OLDEST
    // (evicted) updates and therefore sits below the surviving entries.
    const entries = buffered.map(makeEntry).reverse();
    if (dropped > 0) {
      entries.push({
        id: `flow-${++logSeqRef.current}`,
        at: Date.now(),
        // Synthetic entry — no endpoint to link; the page renders it as a
        // plain muted line.
        endpointId: "",
        kind: "released",
        count: dropped,
        message: `… ${dropped} earlier update${dropped === 1 ? "" : "s"} condensed while paused`,
      });
    }
    setLog((prev) => [...entries, ...prev].slice(0, LOG_MAX));

    if (buffered.length > 0 && !document.hidden) {
      onActivityRef.current(buffered);
    }
  }, [makeEntry]);

  // Sync the pause ref on every change; the stale value read before the
  // write doubles as the previous-value tracker for the resume edge.
  useEffect(() => {
    const wasPaused = pausedRef.current;
    pausedRef.current = paused;
    if (wasPaused && !paused) flushPauseBuffer();
  }, [paused, flushPauseBuffer]);

  /**
   * One full status poll — all endpoint ids, then their status counts —
   * funneled into the shared pipeline. Failures are swallowed: a transient
   * API blip must not tear down the polling loop; the next tick retries.
   */
  const pollStatus = useCallback(async (): Promise<void> => {
    if (pollInFlightRef.current) return;
    pollInFlightRef.current = true;
    try {
      if (!clientRef.current) {
        clientRef.current = new api.Client(api.CookieAuth());
      }
      const client = clientRef.current;
      const ids = await client.getEndpointsAll();
      if (ids.length === 0) {
        lastPollAtRef.current = Date.now();
        return;
      }
      const counts = await client.postApiEndpointStatusCount(ids);
      lastPollAtRef.current = Date.now();
      const snaps = counts
        .map(toSnapshot)
        .filter((s): s is StatusSnapshot => s !== undefined);
      processSnapshots(snaps);
    } catch {
      // Swallowed by design — connectivity state is owned by the hub
      // subscription; a failed poll just means this tick produced no data.
    } finally {
      pollInFlightRef.current = false;
    }
  }, [processSnapshots]);

  // Mount: baseline poll + hub subscription. The poll runs regardless of how
  // the hub negotiation lands, so first paint always has authoritative
  // snapshots; the subscription flips mode on every connectivity transition
  // and stays "connecting" until the first one resolves.
  useEffect(() => {
    let cancelled = false;
    baselinePollRef.current = pollStatus();

    const subscription = subscribeEndpointUpdates(
      (raw) => {
        const snap = toSnapshot(raw);
        if (snap !== undefined) processSnapshots([snap]);
      },
      (state) => {
        if (!cancelled) setMode(state === "connected" ? "live" : "polling");
      },
    );

    return () => {
      cancelled = true;
      subscription.dispose();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Fallback polling — only while degraded. Entering "polling" fires an
  // immediate catch-up poll unless one already ran within the last POLL_MS.
  useEffect(() => {
    if (mode !== "polling") return;
    if (Date.now() - lastPollAtRef.current >= POLL_MS) {
      void pollStatus();
    }
    const handle = window.setInterval(() => {
      void pollStatus();
    }, POLL_MS);
    return () => window.clearInterval(handle);
  }, [mode, pollStatus]);

  // Reconcile poll — always on, drift correction per FR-005. Skips the tick
  // when fallback polling already covered the window so degraded mode never
  // double-polls.
  useEffect(() => {
    const handle = window.setInterval(() => {
      if (Date.now() - lastPollAtRef.current < POLL_MS) return;
      void pollStatus();
    }, RECONCILE_MS);
    return () => window.clearInterval(handle);
  }, [pollStatus]);

  // Counter seed — mount + period change. published/completed/failed come
  // from the metrics window; deferred/deadlettered have no period dimension,
  // so they sum the authoritative snapshots (awaiting the baseline poll on
  // mount so the sums aren't computed against an empty map). Live deltas
  // advance the counters from here.
  useEffect(() => {
    let cancelled = false;
    const seed = async (): Promise<void> => {
      try {
        if (!clientRef.current) {
          clientRef.current = new api.Client(api.CookieAuth());
        }
        const overview = await clientRef.current.getMetricsOverview(period);
        await baselinePollRef.current;
        if (cancelled) return;

        const sum = (
          rows: api.EndpointEventTypeMessageCount[] | undefined,
        ): number => (rows ?? []).reduce((acc, r) => acc + (r.count ?? 0), 0);

        let deferred = 0;
        let deadlettered = 0;
        for (const snap of Object.values(snapshotsRef.current)) {
          deferred += snap.deferred;
          deadlettered += snap.deadlettered;
        }

        setCounters({
          published: sum(overview.published),
          completed: sum(overview.handled),
          failed: sum(overview.failed),
          deferred,
          deadlettered,
        });
      } catch {
        // Seed failure: counters keep their current values and still advance
        // with live deltas — stale-but-moving beats zeroed-and-wrong.
      }
    };
    void seed();
    return () => {
      cancelled = true;
    };
  }, [period]);

  return {
    topology,
    topologyLoading,
    topologyError,
    counters,
    log,
    mode,
    snapshots,
  };
}
