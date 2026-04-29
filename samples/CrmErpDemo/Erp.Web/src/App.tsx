import { Link, NavLink, Route, Routes } from 'react-router-dom';
import CustomersList from './pages/CustomersList';
import CustomerForm from './pages/CustomerForm';
import ContactsList from './pages/ContactsList';
import ContactForm from './pages/ContactForm';

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
          <span className="ml-auto text-xs text-slate-400">NimBus · CRM + ERP demo</span>
        </div>
      </header>
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
