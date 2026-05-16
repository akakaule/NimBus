import { useCallback, useEffect, useRef, useState } from "react";
import * as api from "api-client";

const REFRESH_MS = 5_000;
// How many samples to keep per endpoint — drives the sparkline and the
// "rate / min" calculation. 12 samples at 5 s each = 60 s of history.
const HISTORY_LEN = 12;
// New-failure pulse window. A card that transitions from no-failures to
// failing gets a pulsing ring for this long before settling into static red.
export const NEW_FAILURE_MS = 60_000;
// Acks auto-expire after this long. The operator can also un-ack manually.
export const ACK_TTL_MS = 4 * 60 * 60 * 1000;
// If we haven't successfully refreshed for this long, dim the wall and
// surface a "connection lost" banner — a frozen page that looks healthy is
// the worst possible failure mode for a monitoring wall.
export const STALE_AFTER_MS = 30_000;

const ACK_STORAGE_KEY = "nb.monitor.acks.v1";

export type EndpointSample = {
  t: number;
  failed: number;
  pending: number;
  deferred: number;
};

export type AckRecord = {
  /** Operator-supplied reason (free text). Empty string allowed. */
  reason: string;
  /** Wall-clock ms when the ack happened. */
  ackedAt: number;
  /** Failed count at the moment of ack — used to auto-expire on recovery. */
  failedAtAck: number;
};

export type MonitorEndpoint = {
  id: string;
  /** Latest snapshot from the API. */
  status: api.EndpointStatusCount;
  /** Recent history (oldest → newest), capped at HISTORY_LEN. */
  samples: EndpointSample[];
  /** Wall-clock ms when this endpoint *first* transitioned to failing. */
  firstFailureAt?: number;
  /** Truthy only inside the NEW_FAILURE_MS window after a new failure. */
  isFreshFailure: boolean;
  /** Ack record, if the operator has silenced this endpoint. */
  ack?: AckRecord;
  /** Failures per minute over the available sample window, or undefined. */
  ratePerMin?: number;
};

export type TickerEvent = {
  id: string;
  t: number;
  endpoint: string;
  kind: "failure" | "recovery" | "ack" | "unack";
  detail?: string;
};

const TICKER_MAX = 12;

function loadAcks(): Record<string, AckRecord> {
  if (typeof window === "undefined") return {};
  try {
    const raw = window.localStorage.getItem(ACK_STORAGE_KEY);
    if (!raw) return {};
    const parsed = JSON.parse(raw) as Record<string, AckRecord>;
    return parsed && typeof parsed === "object" ? parsed : {};
  } catch {
    return {};
  }
}

function saveAcks(acks: Record<string, AckRecord>): void {
  if (typeof window === "undefined") return;
  try {
    window.localStorage.setItem(ACK_STORAGE_KEY, JSON.stringify(acks));
  } catch {
    // localStorage can be unavailable (private mode, quota); ignore.
  }
}

export interface UseMonitorDataResult {
  endpoints: MonitorEndpoint[];
  lastRefreshAt: number | undefined;
  loading: boolean;
  error: string | undefined;
  isStale: boolean;
  ticker: TickerEvent[];
  ack: (endpointId: string, reason?: string) => void;
  unack: (endpointId: string) => void;
  refresh: () => Promise<void>;
}

/**
 * Polls the endpoint-status-count API on a fixed cadence and decorates each
 * endpoint with the derived data the Monitor page needs:
 *  - rolling sample history (drives sparklines + rate / min)
 *  - first-detected-failure timestamps (drives "Failing since…" and the
 *    new-failure pulse)
 *  - operator-supplied acks, persisted to localStorage so a refresh on the
 *    wall PC doesn't re-light demo/intentional failures
 *  - a small in-memory ticker fed by sample-diff transitions
 *
 * Designed to keep working on a fixed schedule even if individual fetches
 * fail (we just keep the last good snapshot and flip `isStale` once the gap
 * crosses STALE_AFTER_MS).
 */
export function useMonitorData(): UseMonitorDataResult {
  const [endpoints, setEndpoints] = useState<MonitorEndpoint[]>([]);
  const [lastRefreshAt, setLastRefreshAt] = useState<number | undefined>();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | undefined>();
  const [now, setNow] = useState(Date.now());
  const [ticker, setTicker] = useState<TickerEvent[]>([]);

  // Per-endpoint derived state we maintain across refreshes. Stored in refs
  // because they change on every poll tick but only need to bake into the
  // returned MonitorEndpoint[] — re-rendering on every sample-history mutation
  // would be wasteful.
  const samplesRef = useRef<Record<string, EndpointSample[]>>({});
  const firstFailureRef = useRef<Record<string, number>>({});
  const acksRef = useRef<Record<string, AckRecord>>(loadAcks());
  const clientRef = useRef<api.Client | null>(null);
  const endpointIdsRef = useRef<string[] | null>(null);

  const persistAcks = useCallback(() => {
    saveAcks(acksRef.current);
  }, []);

  const pushTicker = useCallback((event: Omit<TickerEvent, "id">) => {
    setTicker((prev) => {
      const next: TickerEvent = {
        ...event,
        id: `${event.t}-${event.endpoint}-${event.kind}`,
      };
      return [next, ...prev].slice(0, TICKER_MAX);
    });
  }, []);

  const fetchOnce = useCallback(async (): Promise<void> => {
    if (!clientRef.current) {
      clientRef.current = new api.Client(api.CookieAuth());
    }
    const client = clientRef.current;

    let ids = endpointIdsRef.current;
    if (!ids) {
      ids = await client.getEndpointsAll();
      endpointIdsRef.current = ids;
    }

    if (ids.length === 0) {
      setEndpoints([]);
      setLastRefreshAt(Date.now());
      setError(undefined);
      setLoading(false);
      return;
    }

    const snapshot = await client.postApiEndpointStatusCount(ids);
    const t = Date.now();
    const ackChanges: { id: string; clearedReason: "recovery" | "ttl" }[] = [];

    const decorated: MonitorEndpoint[] = snapshot.map((status) => {
      const id = status.endpointId ?? "";
      const failed = status.failedCount ?? 0;
      const pending = status.pendingCount ?? 0;
      const deferred = status.deferredCount ?? 0;
      const sample: EndpointSample = { t, failed, pending, deferred };

      // Append + truncate the sample history for this endpoint.
      const prevSamples = samplesRef.current[id] ?? [];
      const samples = [...prevSamples, sample].slice(-HISTORY_LEN);
      samplesRef.current[id] = samples;

      // Detect failure transitions against the prior sample (if any).
      const prior = prevSamples.length
        ? prevSamples[prevSamples.length - 1]
        : undefined;
      if (prior) {
        if (prior.failed === 0 && failed > 0) {
          firstFailureRef.current[id] = t;
          pushTicker({
            t,
            endpoint: id,
            kind: "failure",
            detail: `${failed.toLocaleString()} failed`,
          });
        } else if (prior.failed > 0 && failed === 0) {
          delete firstFailureRef.current[id];
          pushTicker({ t, endpoint: id, kind: "recovery" });
        }
      } else if (failed > 0 && firstFailureRef.current[id] === undefined) {
        // First time we've seen this endpoint AND it's already failing — we
        // can't tell *when* it started, so use the API's eventTime if it
        // gives us anything, otherwise fall back to "now".
        const seedFromEventTime = status.eventTime?.valueOf();
        firstFailureRef.current[id] = seedFromEventTime ?? t;
      }

      // Resolve / auto-expire acks.
      let ack: AckRecord | undefined = acksRef.current[id];
      if (ack) {
        if (failed === 0 && ack.failedAtAck > 0) {
          delete acksRef.current[id];
          ack = undefined;
          ackChanges.push({ id, clearedReason: "recovery" });
        } else if (t - ack.ackedAt > ACK_TTL_MS) {
          delete acksRef.current[id];
          ack = undefined;
          ackChanges.push({ id, clearedReason: "ttl" });
        }
      }

      const firstFailureAt = firstFailureRef.current[id];
      const isFreshFailure =
        firstFailureAt !== undefined && t - firstFailureAt < NEW_FAILURE_MS;

      // Rate per minute — use the oldest sample we still have to spread the
      // delta over the longest window available, then normalize to /min.
      let ratePerMin: number | undefined;
      if (samples.length >= 2) {
        const oldest = samples[0];
        const dt = sample.t - oldest.t;
        if (dt > 0) {
          ratePerMin = ((failed - oldest.failed) / dt) * 60_000;
        }
      }

      return {
        id,
        status,
        samples,
        firstFailureAt,
        isFreshFailure,
        ack,
        ratePerMin,
      };
    });

    if (ackChanges.length) {
      persistAcks();
      for (const change of ackChanges) {
        pushTicker({
          t,
          endpoint: change.id,
          kind: "unack",
          detail:
            change.clearedReason === "recovery"
              ? "auto-cleared · recovered"
              : "auto-cleared · 4h expiry",
        });
      }
    }

    setEndpoints(decorated);
    setLastRefreshAt(t);
    setError(undefined);
    setLoading(false);
  }, [persistAcks, pushTicker]);

  // Initial load + polling loop. We intentionally swallow per-tick errors and
  // surface them on the page instead of throwing, so a transient API blip
  // doesn't tear down the auto-refresh interval.
  useEffect(() => {
    let cancelled = false;
    const run = async () => {
      try {
        await fetchOnce();
      } catch (err) {
        if (!cancelled) {
          setError(err instanceof Error ? err.message : "Failed to refresh");
          setLoading(false);
        }
      }
    };
    void run();
    const handle = window.setInterval(() => {
      void run();
    }, REFRESH_MS);
    return () => {
      cancelled = true;
      window.clearInterval(handle);
    };
  }, [fetchOnce]);

  // Drive "is this stale?" + the new-failure pulse window off a 1 Hz tick.
  // We re-derive isFreshFailure each render rather than scheduling per-card
  // timeouts because the cardinality is small and the math is trivial.
  useEffect(() => {
    const handle = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(handle);
  }, []);

  const isStale =
    lastRefreshAt !== undefined && now - lastRefreshAt > STALE_AFTER_MS;

  const decoratedEndpoints: MonitorEndpoint[] = endpoints.map((e) => ({
    ...e,
    isFreshFailure:
      e.firstFailureAt !== undefined && now - e.firstFailureAt < NEW_FAILURE_MS,
  }));

  const ack = useCallback(
    (endpointId: string, reason: string = "") => {
      const target = endpoints.find((e) => e.id === endpointId);
      const failed = target?.status.failedCount ?? 0;
      acksRef.current[endpointId] = {
        reason,
        ackedAt: Date.now(),
        failedAtAck: failed,
      };
      persistAcks();
      pushTicker({
        t: Date.now(),
        endpoint: endpointId,
        kind: "ack",
        detail: reason || undefined,
      });
      // Optimistically reflect the ack without waiting for the next poll.
      setEndpoints((prev) =>
        prev.map((e) =>
          e.id === endpointId ? { ...e, ack: acksRef.current[endpointId] } : e,
        ),
      );
    },
    [endpoints, persistAcks, pushTicker],
  );

  const unack = useCallback(
    (endpointId: string) => {
      delete acksRef.current[endpointId];
      persistAcks();
      pushTicker({
        t: Date.now(),
        endpoint: endpointId,
        kind: "unack",
      });
      setEndpoints((prev) =>
        prev.map((e) => (e.id === endpointId ? { ...e, ack: undefined } : e)),
      );
    },
    [persistAcks, pushTicker],
  );

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      await fetchOnce();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to refresh");
      setLoading(false);
    }
  }, [fetchOnce]);

  return {
    endpoints: decoratedEndpoints,
    lastRefreshAt,
    loading,
    error,
    isStale,
    ticker,
    ack,
    unack,
    refresh,
  };
}
