import { useEffect, useState } from 'react';
import { api, CircuitStateSnapshot } from '../../api';

// Circuit-breaker showcase panel: the "CRM API outage" toggle drives sustained
// handler failures on CrmEndpoint (the adapter's HTTP client returns synthetic
// 503s), and the state strip shows the endpoint circuit live — Closed (green),
// Open (red, receiver paused), HalfOpen (amber, probing at one session). State
// is pushed adapter → crm-api and polled here, same cadence as erp-web's
// alerts panel.

const POLL_MS = 3000;

const STATE_STYLES: Record<string, { dot: string; badge: string; label: string; hint: string }> = {
  Closed: {
    dot: 'bg-emerald-500',
    badge: 'bg-emerald-50 text-emerald-700 ring-1 ring-emerald-200',
    label: 'Closed',
    hint: 'Receiving normally.',
  },
  Open: {
    dot: 'bg-red-500 animate-pulse',
    badge: 'bg-red-50 text-red-700 ring-1 ring-red-200',
    label: 'Open',
    hint: 'Receiver paused — messages wait on the subscription, no retry budget burns.',
  },
  HalfOpen: {
    dot: 'bg-amber-500 animate-pulse',
    badge: 'bg-amber-50 text-amber-800 ring-1 ring-amber-300',
    label: 'Half-open',
    hint: 'Probing at one concurrent session — successes close the circuit.',
  },
};

export default function CircuitBreakerPanel() {
  const [enabled, setEnabled] = useState<boolean>(false);
  const [loaded, setLoaded] = useState<boolean>(false);
  const [circuit, setCircuit] = useState<CircuitStateSnapshot | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const m = await api.getErrorMode();
        if (!cancelled) setEnabled(m.enabled);
      } catch {
        /* crm-api unreachable — leave the toggle at its default */
      } finally {
        if (!cancelled) setLoaded(true);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    let cancelled = false;
    const tick = async () => {
      try {
        const s = await api.getCircuitState();
        if (!cancelled) setCircuit(s);
      } catch {
        /* keep the last known state */
      }
    };
    void tick();
    const handle = setInterval(tick, POLL_MS);
    return () => {
      cancelled = true;
      clearInterval(handle);
    };
  }, []);

  const toggle = async () => {
    try {
      const next = await api.setErrorMode(!enabled);
      setEnabled(next.enabled);
    } catch {
      /* ignore — next poll or click will reconcile */
    }
  };

  const style = STATE_STYLES[circuit?.state ?? 'Closed'] ?? STATE_STYLES.Closed;

  return (
    <section className="bg-white border border-slate-200 rounded-md p-4 space-y-4">
      <header className="flex items-center justify-between">
        <div>
          <h2 className="text-sm font-semibold text-slate-700">Circuit breaker · CrmEndpoint</h2>
          <p className="text-xs text-slate-500">
            Simulate a CRM API outage: every adapter call fails with 503, the failure rate opens the
            circuit, and the NimBus receiver pauses instead of burning retries into the dead-letter queue.
          </p>
        </div>
        <button
          type="button"
          onClick={toggle}
          disabled={!loaded}
          className={`px-3 py-1.5 rounded-md text-xs font-medium disabled:opacity-50 ${
            enabled ? 'bg-red-600 text-white' : 'bg-slate-200 text-slate-700'
          }`}
        >
          {`CRM API outage: ${enabled ? 'ON' : 'OFF'}`}
        </button>
      </header>
      <div className="flex items-center gap-3">
        <span className={`inline-flex items-center gap-2 px-2.5 py-1 rounded-md text-xs font-medium ${style.badge}`}>
          <span className={`h-2 w-2 rounded-full ${style.dot}`} />
          {style.label}
        </span>
        <span className="text-xs text-slate-500">{style.hint}</span>
        {circuit && circuit.reason && (
          <span className="ml-auto text-xs text-slate-400 truncate max-w-[24rem]" title={circuit.reason}>
            {circuit.reason}
          </span>
        )}
      </div>
    </section>
  );
}
