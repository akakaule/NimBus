import { useCallback, useEffect, useRef, useState } from 'react';
import { errorMessage, isAbortError } from '../api';

export interface PollResult<T> {
  /** The latest good data; kept when a later poll fails, so the screen never blanks out. */
  data: T | undefined;
  /** The latest poll's error, cleared by the next successful poll. */
  error: string | undefined;
  /** True until the first answer (data or error) for the current key. */
  loading: boolean;
  /** Fetches now; resolves once data fetched after this call has been applied. */
  refresh: () => Promise<void>;
}

interface Snapshot<T> {
  key: string;
  data: T | undefined;
  error: string | undefined;
  loading: boolean;
}

interface Waiter {
  generation: number;
  resolve: () => void;
}

/**
 * Loads data for `key` and, with an interval, keeps polling it so integration updates appear
 * without a reload. Polls never overlap, pause while the tab is hidden and fire immediately when
 * it becomes visible again (the presenter switching back from the Business Central tab).
 */
export function usePolling<T>(
  key: string,
  fetcher: (signal: AbortSignal) => Promise<T>,
  intervalMs?: number,
): PollResult<T> {
  const fetcherRef = useRef(fetcher);
  useEffect(() => {
    fetcherRef.current = fetcher;
  });

  const [snapshot, setSnapshot] = useState<Snapshot<T>>({ key, data: undefined, error: undefined, loading: true });
  const [restart, setRestart] = useState(0);
  // Bumped by refresh(): answers to requests started before it are stale and dropped.
  const generation = useRef(0);
  const waiters = useRef<Waiter[]>([]);

  useEffect(() => {
    let cancelled = false;
    let inFlight = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let controller: AbortController | undefined;

    const release = (completed: number) => {
      const ready = waiters.current.filter((w) => w.generation <= completed);
      waiters.current = waiters.current.filter((w) => w.generation > completed);
      ready.forEach((w) => w.resolve());
    };

    const schedule = () => {
      if (!cancelled && intervalMs) timer = setTimeout(tick, intervalMs);
    };

    const run = async () => {
      if (cancelled || inFlight) return;
      clearTimeout(timer);
      inFlight = true;
      const started = generation.current;
      controller = new AbortController();
      try {
        const data = await fetcherRef.current(controller.signal);
        if (cancelled || started < generation.current) return;
        setSnapshot({ key, data, error: undefined, loading: false });
      } catch (error) {
        if (cancelled || started < generation.current || isAbortError(error)) return;
        setSnapshot((previous) => ({
          key,
          data: previous.key === key ? previous.data : undefined,
          error: errorMessage(error),
          loading: false,
        }));
      } finally {
        inFlight = false;
      }
      release(started);
      schedule();
    };

    function tick() {
      if (document.visibilityState === 'hidden') schedule();
      else void run();
    }

    const onVisibilityChange = () => {
      if (intervalMs && document.visibilityState === 'visible') void run();
    };

    void run();
    document.addEventListener('visibilitychange', onVisibilityChange);
    return () => {
      cancelled = true;
      clearTimeout(timer);
      controller?.abort();
      document.removeEventListener('visibilitychange', onVisibilityChange);
    };
  }, [key, intervalMs, restart]);

  // Nobody should wait forever on a screen that is gone.
  useEffect(
    () => () => {
      waiters.current.forEach((w) => w.resolve());
      waiters.current = [];
    },
    [],
  );

  const refresh = useCallback(
    () =>
      new Promise<void>((resolve) => {
        generation.current += 1;
        waiters.current.push({ generation: generation.current, resolve });
        setRestart((n) => n + 1);
      }),
    [],
  );

  const current: Snapshot<T> =
    snapshot.key === key ? snapshot : { key, data: undefined, error: undefined, loading: true };
  return { data: current.data, error: current.error, loading: current.loading, refresh };
}
