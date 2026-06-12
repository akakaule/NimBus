import { useEffect, useRef, useState } from 'react';
import { api } from '../../api';

// Demo control for the ERP adapter's artificial per-message processing delay.
// The adapter's ProcessingDelayMiddleware polls /api/admin/processing-delay and,
// when enabled, holds each inbound message for `delayMs` before its handler runs —
// so message processing visibly takes time on the Flow monitor.

const DEBOUNCE_MS = 300;
const MIN_MS = 100;
const MAX_MS = 100_000;

function formatDelay(ms: number): string {
  return ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(ms % 1000 === 0 ? 0 : 1)} s`;
}

export default function ProcessingDelayPanel() {
  const [enabled, setEnabled] = useState<boolean>(false);
  const [delayMs, setDelayMs] = useState<number>(2000);
  const [loaded, setLoaded] = useState<boolean>(false);
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const skipNextSendRef = useRef<boolean>(true);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const m = await api.getProcessingDelay();
        if (cancelled) return;
        setEnabled(m.enabled);
        setDelayMs(Math.max(MIN_MS, Math.min(MAX_MS, Math.round(m.delayMs))));
        setLoaded(true);
      } catch {
        setLoaded(true);
      }
    })();
    return () => { cancelled = true; };
  }, []);

  useEffect(() => {
    if (!loaded) return;
    // First render after the GET seeds initial values; don't echo it back.
    if (skipNextSendRef.current) {
      skipNextSendRef.current = false;
      return;
    }
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => {
      api.setProcessingDelay({ enabled, delayMs })
        .catch(() => { /* ignore — UI will reflect next refresh */ });
    }, DEBOUNCE_MS);
    return () => {
      if (debounceRef.current) clearTimeout(debounceRef.current);
    };
  }, [enabled, delayMs, loaded]);

  return (
    <section className="bg-white border border-slate-200 rounded-md p-4 space-y-4">
      <header className="flex items-center justify-between">
        <div>
          <h2 className="text-sm font-semibold text-slate-700">Processing delay</h2>
          <p className="text-xs text-slate-500">
            When enabled, the ERP adapter holds each inbound message for the configured time
            before handling it — simulating a slow consumer so processing takes visible time.
          </p>
        </div>
        <button
          type="button"
          onClick={() => setEnabled((v) => !v)}
          disabled={!loaded}
          className={`px-3 py-1.5 rounded-md text-xs font-medium disabled:opacity-50 ${
            enabled
              ? 'bg-indigo-600 text-white hover:bg-indigo-700'
              : 'bg-slate-200 text-slate-700 hover:bg-slate-300'
          }`}
          title="When ON, every inbound ERP message waits the configured delay before its handler runs."
        >
          {`Processing delay: ${enabled ? 'ON' : 'OFF'}`}
        </button>
      </header>

      <div>
        <div className="flex items-center justify-between text-xs text-slate-600">
          <label htmlFor="processing-delay">Delay per message</label>
          <span className="font-mono">{formatDelay(delayMs)}</span>
        </div>
        <input
          id="processing-delay"
          type="range"
          min={MIN_MS}
          max={MAX_MS}
          step={100}
          value={delayMs}
          onChange={(e) => setDelayMs(Number(e.target.value))}
          disabled={!loaded}
          className="w-full accent-indigo-600"
        />
        <div className="flex justify-between text-[10px] text-slate-400 font-mono">
          <span>100 ms</span>
          <span>100 s</span>
        </div>
      </div>
    </section>
  );
}
