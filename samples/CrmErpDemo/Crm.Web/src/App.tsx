import { Link, NavLink, Route, Routes } from 'react-router-dom';
import AccountsList from './pages/AccountsList';
import AccountForm from './pages/AccountForm';
import ContactsList from './pages/ContactsList';
import ContactForm from './pages/ContactForm';
import { simulator, useSimulator } from './simulator';

const tabClass = ({ isActive }: { isActive: boolean }) =>
  `px-4 py-2 rounded-md text-sm font-medium ${
    isActive ? 'bg-blue-600 text-white' : 'text-slate-600 hover:bg-slate-200'
  }`;

export default function App() {
  const sim = useSimulator();
  return (
    <div className="min-h-full">
      <header className="bg-white border-b border-slate-200">
        <div className="max-w-5xl mx-auto px-6 py-4 flex items-center gap-6">
          <Link to="/" className="text-xl font-semibold text-blue-700">CRM Demo</Link>
          <nav className="flex gap-2">
            <NavLink to="/accounts" className={tabClass}>Accounts</NavLink>
            <NavLink to="/contacts" className={tabClass}>Contacts</NavLink>
          </nav>
          <button
            type="button"
            onClick={() => simulator.toggle()}
            title="Auto-generate account & contact activity to drive Service Bus traffic"
            className={`ml-auto inline-flex items-center gap-2 px-3 py-1.5 rounded-md text-sm font-medium transition-colors ${
              sim.running
                ? 'bg-amber-100 text-amber-800 ring-1 ring-amber-300'
                : 'bg-slate-100 text-slate-600 hover:bg-slate-200 border border-slate-200'
            }`}
          >
            <span className={`h-2 w-2 rounded-full ${sim.running ? 'bg-amber-500 animate-pulse' : 'bg-slate-400'}`} />
            {sim.running ? `Simulating · ${sim.actions}` : 'Simulate'}
          </button>
          <span className="text-xs text-slate-400">NimBus · CRM + ERP demo</span>
        </div>
      </header>
      <main className="max-w-5xl mx-auto px-6 py-8">
        <Routes>
          <Route path="/" element={<AccountsList />} />
          <Route path="/accounts" element={<AccountsList />} />
          <Route path="/accounts/new" element={<AccountForm />} />
          <Route path="/accounts/:id" element={<AccountForm />} />
          <Route path="/contacts" element={<ContactsList />} />
          <Route path="/contacts/new" element={<ContactForm />} />
          <Route path="/contacts/:id" element={<ContactForm />} />
        </Routes>
      </main>
    </div>
  );
}
