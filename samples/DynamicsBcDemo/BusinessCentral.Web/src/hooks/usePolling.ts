import { useCallback, useEffect, useRef, useState } from 'react';

interface Snapshot<T> {
  key: string;
  data: T | undefined;
  error: Error | undefined;
  loading: boolean;
}

export interface Polled<T> {
  data: T | undefined;
  /** The last failure; kept next to the last good data so a hiccup doesn't blank the page. */
  error: Error | undefined;
  loading: boolean;
  /** Loads again now (after the user changed something) instead of waiting for the next tick. */
  refresh: () => Promise<void>;
  /** Shows a fresher copy right away, e.g. the body a PUT returned. */
  replace: (data: T) => void;
}

function asError(error: unknown): Error {
  return error instanceof Error ? error : new Error(String(error));
}

/**
 * Loads data and reloads it every `intervalMs` (null = load once), so changes that arrive through
 * the integration show up without a manual refresh. A new `key` (e.g. another record id) starts
 * over. Polls never overlap: the next one is scheduled when the previous one has finished.
 */
export function usePolling<T>(
  load: (signal: AbortSignal) => Promise<T>,
  intervalMs: number | null,
  key = '',
): Polled<T> {
  const [snapshot, setSnapshot] = useState<Snapshot<T>>({ key, data: undefined, error: undefined, loading: true });
  const loadRef = useRef(load);
  const keyRef = useRef(key);
  const latestRequest = useRef(0);
  const inFlight = useRef(new Set<AbortController>());

  useEffect(() => {
    loadRef.current = load;
    keyRef.current = key;
  });

  const fetchNow = useCallback(async () => {
    const requestId = ++latestRequest.current;
    const requestKey = keyRef.current;
    const controller = new AbortController();
    inFlight.current.add(controller);
    try {
      const data = await loadRef.current(controller.signal);
      if (requestId === latestRequest.current) {
        setSnapshot({ key: requestKey, data, error: undefined, loading: false });
      }
    } catch (error) {
      if (controller.signal.aborted || requestId !== latestRequest.current) return;
      setSnapshot((previous) => ({
        key: requestKey,
        data: previous.key === requestKey ? previous.data : undefined,
        error: asError(error),
        loading: false,
      }));
    } finally {
      inFlight.current.delete(controller);
    }
  }, []);

  useEffect(() => {
    keyRef.current = key;
    const controllers = inFlight.current;
    let stopped = false;
    let timer: number | undefined;

    const tick = async () => {
      await fetchNow();
      if (!stopped && intervalMs !== null) timer = window.setTimeout(tick, intervalMs);
    };
    void tick();

    return () => {
      stopped = true;
      window.clearTimeout(timer);
      latestRequest.current++;
      controllers.forEach((controller) => controller.abort());
      controllers.clear();
    };
  }, [key, intervalMs, fetchNow]);

  const replace = useCallback((data: T) => {
    latestRequest.current++;
    setSnapshot({ key: keyRef.current, data, error: undefined, loading: false });
  }, []);

  // Until the first load for a new key lands, report "loading" rather than the previous record.
  const current = snapshot.key === key ? snapshot : { key, data: undefined, error: undefined, loading: true };
  return { data: current.data, error: current.error, loading: current.loading, refresh: fetchNow, replace };
}
