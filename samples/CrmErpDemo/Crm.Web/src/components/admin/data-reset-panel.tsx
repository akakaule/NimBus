import { useState } from 'react';
import { api } from '../../api';

// Demo reset: deletes every CRM business row (accounts, contacts, audit) plus
// the NimBus inbox-dedup rows in the CRM database. Irreversible; publishes no
// delete events, so NimBus message history and the ERP side are untouched.

export default function DataResetPanel() {
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<string | null>(null);

  const wipe = async () => {
    const confirmed = window.confirm(
      'Delete ALL CRM data?\n\n' +
        'Removes every account, contact and audit row (and NimBus inbox rows in this database). ' +
        'No delete events are published — NimBus message history and the ERP side stay as they are.\n\n' +
        'This cannot be undone.',
    );
    if (!confirmed) return;
    setBusy(true);
    setResult(null);
    try {
      const r = await api.deleteAllData();
      setResult(`Deleted ${r.accounts} accounts · ${r.contacts} contacts · ${r.audits} audit rows`);
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
            Wipe every CRM account, contact and audit row for a clean demo slate. Irreversible ·
            no delete events published.
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
