import { makeStyles, tokens } from '@fluentui/react-components';
import { useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { api, type Contact } from '../api';
import { ProspectTag } from '../components/Badges';
import { BcTable, type Column } from '../components/BcTable';
import { ListToolbar, PageBody, Panel } from '../components/Layout';
import { FilterChip, RecordCount, SearchInput } from '../components/ListControls';
import { PageHeader } from '../components/PageHeader';
import { RouterLink } from '../components/RouterLink';
import { LoadError, Loading, StaleNotice } from '../components/States';
import { formatDateTime } from '../format';
import { usePolling } from '../hooks/usePolling';

const useStyles = makeStyles({
  guid: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
});

function CrmAccountId({ id }: { id: string | null }) {
  const styles = useStyles();
  if (!id) return null;
  return (
    <span className={styles.guid} title={id}>
      {id.slice(0, 8)}…
    </span>
  );
}

const columns: Column<Contact>[] = [
  { key: 'number', header: 'No.', width: '100px', render: (c) => c.number },
  { key: 'name', header: 'Name', render: (c) => c.displayName },
  { key: 'type', header: 'Type', width: '90px', render: (c) => c.type },
  { key: 'city', header: 'City', width: '120px', render: (c) => c.city },
  { key: 'country', header: 'Country/Region', width: '120px', render: (c) => c.countryCode },
  { key: 'crm', header: 'CRM Account ID', width: '130px', render: (c) => <CrmAccountId id={c.crmAccountId} /> },
  {
    key: 'customer',
    header: 'Customer No.',
    width: '120px',
    render: (c) =>
      c.customerId && c.customerNumber ? (
        <RouterLink to={`/customers/${c.customerId}`}>{c.customerNumber}</RouterLink>
      ) : (
        <ProspectTag />
      ),
  },
  { key: 'template', header: 'Customer Template', width: '140px', render: (c) => c.customerTemplateCode },
  { key: 'modified', header: 'Last Modified', width: '170px', render: (c) => formatDateTime(c.lastModifiedDateTime) },
];

function matches(contact: Contact, search: string): boolean {
  return [contact.number, contact.displayName, contact.city, contact.countryCode, contact.customerNumber, contact.crmAccountId]
    .filter(Boolean)
    .join(' ')
    .toLowerCase()
    .includes(search.toLowerCase());
}

/** Contacts (read-only): the prospects BC quotes before they buy. */
export default function ContactsList() {
  const [params, setParams] = useSearchParams();
  const prospectsOnly = params.get('prospects') === '1';
  const [search, setSearch] = useState('');
  const { data, error, refresh } = usePolling((signal) => api.contacts(signal), 3000);

  const rows = useMemo(
    () => (data ?? []).filter((c) => (!prospectsOnly || !c.customerId) && (!search || matches(c, search))),
    [data, prospectsOnly, search],
  );

  return (
    <>
      <PageHeader title="Contacts" />
      <PageBody>
        {!data ? (
          error ? <LoadError error={error} onRetry={refresh} /> : <Loading />
        ) : (
          <>
            <StaleNotice error={error} />
            <Panel>
              <ListToolbar>
                <SearchInput value={search} onChange={setSearch} placeholder="Search contacts" />
                {prospectsOnly && <FilterChip label="Prospects (not yet customers)" onClear={() => setParams({})} />}
                <RecordCount shown={rows.length} total={data.length} />
              </ListToolbar>
              <BcTable
                aria-label="Contacts"
                data-testid="contacts-list"
                columns={columns}
                rows={rows}
                rowKey={(c) => c.id}
                empty={
                  data.length === 0
                    ? 'No contacts yet. Business Central creates one when Dynamics 365 asks to quote a new prospect.'
                    : 'No contacts match the filter.'
                }
              />
            </Panel>
          </>
        )}
      </PageBody>
    </>
  );
}
