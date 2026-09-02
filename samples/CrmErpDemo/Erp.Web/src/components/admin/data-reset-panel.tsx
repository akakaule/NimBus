import { useState } from 'react';
import { api } from '../../api';

// Demo reset: deletes every ERP business row (customers, contacts, audit) plus
// the NimBus outbox rows in the ERP database — pending outbox rows would
// otherwise publish ghost events about entities that no longer exist.
// Irreversible; publishes no delete events, so NimBus message history and the
// CRM side are untouched.

export default function DataResetPanel() {
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<string | null>(null);

  const wipe = async () => {
    const confirmed = window.confirm(
      'Delete ALL ERP data?\n\n' +
        'Removes every customer, contact and audit row, and clears pending NimBus outbox rows in this database. ' +
        'No delete events are published — NimBus message history and the CRM side stay as they are.\n\n' +
        'This cannot be undone.',
    );
    if (!confirmed) return;
    setBusy(true);
    setResult(null);
    try {
      const r = await api.deleteAllData();
      setResult(
        `Deleted ${r.customers} customers · ${r.contacts} contacts · ${r.audits} audit rows · ${r.outboxRows} outbox rows`,
      );
    } catch (err) {
      setResult(`Reset failed: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="bg-white border border-red-200 rounded-md p-4 space-y-2">
      <header className="flex items-center justify-between">
        <div>
          <h2 className="text-sm font-semibold text-slate-700">Danger zone</h2>
          <p className="text-xs text-slate-500">
            Wipe every ERP customer, contact and audit row (plus pending outbox rows) for a clean
            demo slate. Irreversible · no delete events published.
          </p>
        </div>
        <button
          type="button"
          onClick={wipe}
          disabled={busy}
          className="px-3 py-1.5 rounded-md text-xs font-medium bg-red-600 text-white disabled:opacity-50"
        >
          {busy ? 'Deleting…' : 'Delete all data'}
        </button>
      </header>
      {result && <p className="text-xs text-slate-500 font-mono">{result}</p>}
    </section>
  );
}
