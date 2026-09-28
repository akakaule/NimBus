import { FluentProvider, type Theme } from '@fluentui/react-components';
import type { ReactNode } from 'react';
import { Route, Routes } from 'react-router-dom';
import { BcShell } from './components/BcShell';
import ContactsList from './pages/ContactsList';
import CustomerCard from './pages/CustomerCard';
import CustomersList from './pages/CustomersList';
import AlertsChannel from './pages/demo/AlertsChannel';
import DemoCockpit from './pages/demo/DemoCockpit';
import NotFound from './pages/NotFound';
import OrderCard from './pages/OrderCard';
import OrdersList from './pages/OrdersList';
import QuoteCard from './pages/QuoteCard';
import QuotesList from './pages/QuotesList';
import RoleCenter from './pages/RoleCenter';
import SalespeopleList from './pages/SalespeopleList';
import { bcTheme, teamsTheme } from './theme';

// The provider's minimum height comes from index.css, not a className: FluentProvider copies its
// className onto the portal nodes of dialogs, menus and tooltips, which would then cover the page.
function Themed({ theme, children }: { theme: Theme; children: ReactNode }) {
  return <FluentProvider theme={theme}>{children}</FluentProvider>;
}

export default function App() {
  return (
    <Routes>
      {/* Hidden presenter pages: no BC chrome and no navigation entry. */}
      <Route
        path="/demo"
        element={
          <Themed theme={bcTheme}>
            <DemoCockpit />
          </Themed>
        }
      />
      <Route
        path="/demo/alerts"
        element={
          <Themed theme={teamsTheme}>
            <AlertsChannel />
          </Themed>
        }
      />

      <Route
        element={
          <Themed theme={bcTheme}>
            <BcShell />
          </Themed>
        }
      >
        <Route index element={<RoleCenter />} />
        <Route path="quotes" element={<QuotesList />} />
        <Route path="quotes/:id" element={<QuoteCard />} />
        <Route path="customers" element={<CustomersList />} />
        <Route path="customers/:id" element={<CustomerCard />} />
        <Route path="contacts" element={<ContactsList />} />
        <Route path="salespeople" element={<SalespeopleList />} />
        <Route path="orders" element={<OrdersList />} />
        <Route path="orders/:id" element={<OrderCard />} />
        <Route path="*" element={<NotFound />} />
      </Route>
    </Routes>
  );
}
