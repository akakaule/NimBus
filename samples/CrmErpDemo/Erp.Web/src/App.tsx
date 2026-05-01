import { useEffect, useState } from 'react';
import { Link, NavLink, Route, Routes } from 'react-router-dom';
import CustomersList from './pages/CustomersList';
import CustomerForm from './pages/CustomerForm';
import ContactsList from './pages/ContactsList';
import ContactForm from './pages/ContactForm';
import { api } from './api';

const tabClass = ({ isActive }: { isActive: boolean }) =>
  `px-4 py-2 rounded-md text-sm font-medium ${
    isActive ? 'bg-emerald-600 text-white' : 'text-slate-600 hover:bg-slate-200'
  }`;

export default function App() {
  return (
    <div className="min-h-full">
      <header className="bg-white border-b border-slate-200">
        <div className="max-w-5xl mx-auto px-6 py-4 flex items-center gap-6">
          <Link to="/" className="text-xl font-semibold text-emerald-700">ERP Demo</Link>
          <nav className="flex gap-2">
            <NavLink to="/customers" className={tabClass}>Customers</NavLink>
            <NavLink to="/contacts" className={tabClass}>Contacts</NavLink>
          </nav>
          <div className="ml-auto flex items-center gap-4">
            <ServiceModeToggle />
            <ErrorModeToggle />
            <span className="text-xs text-slate-400">NimBus · CRM + ERP demo</span>
          </div>
        </div>
      </header>
      <ServiceModeBanner />
      <ErrorModeBanner />
      <main className="max-w-5xl mx-auto px-6 py-8">
        <Routes>
          <Route path="/" element={<CustomersList />} />
          <Route path="/customers" element={<CustomersList />} />
          <Route path="/customers/new" element={<CustomerForm />} />
          <Route path="/customers/:id" element={<CustomerForm />} />
          <Route path="/contacts" element={<ContactsList />} />
          <Route path="/contacts/new" element={<ContactForm />} />
          <Route path="/contacts/:id" element={<ContactForm />} />
        </Routes>
      </main>
    </div>
  );
}

function useServiceMode() {
  const [enabled, setEnabled] = useState<boolean | null>(null);
  const [busy, setBusy] = useState(false);

  async function refresh() {
    try { setEnabled((await api.getServiceMode()).enabled); } catch { /* ignore */ }
  }

  async function toggle() {
    if (enabled === null || busy) return;
    setBusy(true);
    try { setEnabled((await api.setServiceMode(!enabled)).enabled); }
    finally { setBusy(false); }
  }

  useEffect(() => {
    refresh();
    const t = setInterval(refresh, 3000);
    return () => clearInterval(t);
  }, []);

  return { enabled, busy, toggle };
}

function ServiceModeToggle() {
  const { enabled, busy, toggle } = useServiceMode();
  const label = enabled === null ? 'Service mode' : enabled ? 'Service mode: ON' : 'Service mode: OFF';
  const cls = enabled
    ? 'bg-rose-600 text-white hover:bg-rose-700'
    : 'bg-slate-200 text-slate-700 hover:bg-slate-300';
  return (
    <button
      type="button"
      onClick={toggle}
      disabled={enabled === null || busy}
      className={`px-3 py-1.5 rounded-md text-xs font-medium disabled:opacity-50 ${cls}`}
      title="When ON, the ERP adapter rejects every inbound message — useful for showing the dead-letter / resubmit flow."
    >
      {label}
    </button>
  );
}

function ServiceModeBanner() {
  const [enabled, setEnabled] = useState(false);
  useEffect(() => {
    const tick = async () => {
      try { setEnabled((await api.getServiceMode()).enabled); } catch { /* ignore */ }
    };
    tick();
    const t = setInterval(tick, 3000);
    return () => clearInterval(t);
  }, []);
  if (!enabled) return null;
  return (
    <div className="bg-rose-600 text-white text-sm">
      <div className="max-w-5xl mx-auto px-6 py-2">
        ERP is in <strong>service mode</strong> — the ERP adapter rejects every inbound message until the toggle is turned off.
      </div>
    </div>
  );
}

function useErrorMode() {
  const [enabled, setEnabled] = useState<boolean | null>(null);
  const [busy, setBusy] = useState(false);

  async function refresh() {
    try { setEnabled((await api.getErrorMode()).enabled); } catch { /* ignore */ }
  }

  async function toggle() {
    if (enabled === null || busy) return;
    setBusy(true);
    try { setEnabled((await api.setErrorMode(!enabled)).enabled); }
    finally { setBusy(false); }
  }

  useEffect(() => {
    refresh();
    const t = setInterval(refresh, 3000);
    return () => clearInterval(t);
  }, []);

  return { enabled, busy, toggle };
}

function ErrorModeToggle() {
  const { enabled, busy, toggle } = useErrorMode();
  const label = enabled === null ? 'Error mode' : enabled ? 'Error mode: ON' : 'Error mode: OFF';
  const cls = enabled
    ? 'bg-amber-600 text-white hover:bg-amber-700'
    : 'bg-slate-200 text-slate-700 hover:bg-slate-300';
  return (
    <button
      type="button"
      onClick={toggle}
      disabled={enabled === null || busy}
      className={`px-3 py-1.5 rounded-md text-xs font-medium disabled:opacity-50 ${cls}`}
      title="When ON, every ERP adapter message handler throws an exception."
    >
      {label}
    </button>
  );
}

function ErrorModeBanner() {
  const [enabled, setEnabled] = useState(false);
  useEffect(() => {
    const tick = async () => {
      try { setEnabled((await api.getErrorMode()).enabled); } catch { /* ignore */ }
    };
    tick();
    const t = setInterval(tick, 3000);
    return () => clearInterval(t);
  }, []);
  if (!enabled) return null;
  return (
    <div className="bg-amber-600 text-white text-sm">
      <div className="max-w-5xl mx-auto px-6 py-2">
        ERP is in <strong>error mode</strong> — message handlers throw on every inbound message until the toggle is turned off.
      </div>
    </div>
  );
}
