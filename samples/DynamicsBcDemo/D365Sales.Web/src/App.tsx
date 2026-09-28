import { StrictMode } from 'react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { BrowserRouter, Route, Routes, useParams } from 'react-router-dom';
import { Shell } from './components/Shell';
import { AccountForm } from './pages/AccountForm';
import { AccountsList } from './pages/AccountsList';
import { Dashboard } from './pages/Dashboard';
import { LeadForm } from './pages/LeadForm';
import { LeadsList } from './pages/LeadsList';
import { NotFound } from './pages/NotFound';
import { OpportunitiesList } from './pages/OpportunitiesList';
import { OpportunityForm } from './pages/OpportunityForm';
import { CurrentUserProvider } from './user';

// Keyed by id, so moving between records starts each form fresh instead of showing the last one.
function LeadRoute() {
  const { id = '' } = useParams();
  return <LeadForm key={id} id={id} />;
}

function OpportunityRoute() {
  const { id = '' } = useParams();
  return <OpportunityForm key={id} id={id} />;
}

function AccountRoute() {
  const { id = '' } = useParams();
  return <AccountForm key={id} id={id} />;
}

export function App() {
  // FluentProvider sits outside StrictMode on purpose: StrictMode's development-only double mount of
  // the provider makes Fluent's focus tracking log "Keyborg instance … is being disposed incorrectly".
  // Its height comes from index.css (see there why not from a className).
  return (
    <FluentProvider theme={webLightTheme}>
      <StrictMode>
        <CurrentUserProvider>
          <BrowserRouter>
            <Shell>
              <Routes>
                <Route path="/" element={<Dashboard />} />
                <Route path="/leads" element={<LeadsList />} />
                <Route path="/leads/:id" element={<LeadRoute />} />
                <Route path="/opportunities" element={<OpportunitiesList />} />
                <Route path="/opportunities/:id" element={<OpportunityRoute />} />
                <Route path="/accounts" element={<AccountsList />} />
                <Route path="/accounts/:id" element={<AccountRoute />} />
                <Route path="*" element={<NotFound />} />
              </Routes>
            </Shell>
          </BrowserRouter>
        </CurrentUserProvider>
      </StrictMode>
    </FluentProvider>
  );
}
